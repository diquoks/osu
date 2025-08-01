// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Extensions.ObjectExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input.Bindings;
using osu.Framework.Input.Events;
using osu.Framework.Localisation;
using osu.Framework.Utils;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Database;
using osu.Game.Extensions;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Graphics.Sprites;
using osu.Game.Input.Bindings;
using osu.Game.Localisation;
using osu.Game.Overlays;
using osu.Game.Overlays.Settings;
using osu.Game.Overlays.Settings.Sections.Audio;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Ranking.Statistics;
using osuTK;

namespace osu.Game.Screens.Play.PlayerSettings
{
    public partial class BeatmapOffsetControl : CompositeDrawable, IKeyBindingHandler<GlobalAction>
    {
        public Bindable<ScoreInfo?> ReferenceScore { get; } = new Bindable<ScoreInfo?>();

        private Bindable<ScoreInfo?> lastAppliedScore { get; } = new Bindable<ScoreInfo?>();

        public BindableDouble Current { get; } = new BindableDouble
        {
            MinValue = -50,
            MaxValue = 50,
            Precision = 0.1,
        };

        private readonly FillFlowContainer referenceScoreContainer;

        [Resolved]
        private RealmAccess realm { get; set; } = null!;

        [Resolved]
        private IBindable<WorkingBeatmap> beatmap { get; set; } = null!;

        [Resolved]
        private OsuColour colours { get; set; } = null!;

        [Resolved]
        private Player? player { get; set; }

        private double lastPlayMedian;
        private double lastPlayBeatmapOffset;
        private HitEventTimingDistributionGraph? lastPlayGraph;

        private SettingsButton? calibrateFromLastPlayButton;

        private IDisposable? beatmapOffsetSubscription;

        private Task? realmWriteTask;
        private ScoreInfo? lastValidScore;

        public BeatmapOffsetControl()
        {
            RelativeSizeAxes = Axes.X;
            AutoSizeAxes = Axes.Y;

            InternalChild = new FillFlowContainer
            {
                RelativeSizeAxes = Axes.X,
                AutoSizeAxes = Axes.Y,
                Direction = FillDirection.Vertical,
                Spacing = new Vector2(10),
                Children = new Drawable[]
                {
                    new OffsetSliderBar
                    {
                        KeyboardStep = 5,
                        LabelText = BeatmapOffsetControlStrings.AudioOffsetThisBeatmap,
                        Current = Current,
                    },
                    referenceScoreContainer = new FillFlowContainer
                    {
                        Spacing = new Vector2(10),
                        Direction = FillDirection.Vertical,
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                    },
                }
            };
        }

        [BackgroundDependencyLoader]
        private void load(SessionStatics statics)
        {
            statics.BindWith(Static.LastAppliedOffsetScore, lastAppliedScore);
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();

            beatmapOffsetSubscription = realm.SubscribeToPropertyChanged(
                r => r.Find<BeatmapInfo>(beatmap.Value.BeatmapInfo.ID)?.UserSettings,
                settings => settings.Offset,
                val =>
                {
                    // At the point we reach here, it's not guaranteed that all realm writes have taken place (there may be some in-flight).
                    // We are only aware of writes that originated from our own flow, so if we do see one that's active we can avoid handling the feedback value arriving.
                    if (realmWriteTask == null)
                    {
                        Current.Disabled = false;
                        Current.Value = val;
                        Current.Disabled = allowOffsetAdjust;
                    }

                    if (realmWriteTask?.IsCompleted == true)
                    {
                        // we can also mark any in-flight write that is managed locally as "seen" and start handling any incoming changes again.
                        realmWriteTask = null;
                    }
                });

            Current.BindValueChanged(currentChanged);
            ReferenceScore.BindValueChanged(scoreChanged, true);
        }

        // the last play graph is relative to the offset at the point of the last play, so we need to factor that out for some usages.
        private double adjustmentSinceLastPlay => lastPlayBeatmapOffset - Current.Value;

        private void currentChanged(ValueChangedEvent<double> offset)
        {
            Scheduler.AddOnce(updateOffset);

            void updateOffset()
            {
                // Negative is applied here because the play graph is considering a hit offset, not track (as we currently use for clocks).
                lastPlayGraph?.UpdateOffset(-adjustmentSinceLastPlay);

                // ensure the previous write has completed. ignoring performance concerns, if we don't do this, the async writes could be out of sequence.
                if (realmWriteTask?.IsCompleted == false)
                {
                    Scheduler.AddOnce(updateOffset);
                    return;
                }

                realmWriteTask = realm.WriteAsync(r =>
                {
                    var setInfo = r.Find<BeatmapSetInfo>(beatmap.Value.BeatmapSetInfo.ID);

                    if (setInfo == null) // only the case for tests.
                        return;

                    // Apply to all difficulties in a beatmap set if they have the same audio
                    // (they generally always share timing).
                    foreach (var b in setInfo.Beatmaps)
                    {
                        BeatmapUserSettings userSettings = b.UserSettings;
                        double val = Current.Value;

                        if (userSettings.Offset != val && b.AudioEquals(beatmap.Value.BeatmapInfo))
                            userSettings.Offset = val;
                    }
                });
            }
        }

        private void scoreChanged(ValueChangedEvent<ScoreInfo?> score)
        {
            if (score.NewValue == null)
                return;

            if (score.NewValue.Equals(lastAppliedScore.Value))
                return;

            if (!score.NewValue.BeatmapInfo.AsNonNull().Equals(beatmap.Value.BeatmapInfo))
                return;

            if (score.NewValue.Mods.Any(m => !m.UserPlayable || m is IHasNoTimedInputs))
                return;

            var hitEvents = score.NewValue.HitEvents;

            if (!(hitEvents.CalculateMedianHitError() is double median))
                return;

            // affecting unstable rate here is used as a substitute of determining if a hit event represents a *timed* hit event,
            // i.e. an user input that the user had to *time to the track*,
            // i.e. one that it *makes sense to use* when doing anything with timing and offsets.
            bool hasEnoughUsableEvents = hitEvents.Count(HitEventExtensions.AffectsUnstableRate) >= 50;

            // If we already have an old score with enough hit events and the new score doesn't have enough, continue displaying the old one rather than showing the user "play too short" message.
            if (lastValidScore != null && !hasEnoughUsableEvents)
                return;

            referenceScoreContainer.Children = new Drawable[]
            {
                new OsuSpriteText
                {
                    Text = BeatmapOffsetControlStrings.PreviousPlay
                },
            };

            if (!hasEnoughUsableEvents)
            {
                referenceScoreContainer.AddRange(new Drawable[]
                {
                    new OsuTextFlowContainer
                    {
                        RelativeSizeAxes = Axes.X,
                        AutoSizeAxes = Axes.Y,
                        Colour = colours.Red1,
                        Text = BeatmapOffsetControlStrings.PreviousPlayTooShortToUseForCalibration
                    },
                });

                return;
            }

            lastValidScore = score.NewValue!;
            lastPlayMedian = median;
            lastPlayBeatmapOffset = Current.Value;

            LinkFlowContainer globalOffsetText;

            referenceScoreContainer.AddRange(new Drawable[]
            {
                lastPlayGraph = new HitEventTimingDistributionGraph(hitEvents)
                {
                    RelativeSizeAxes = Axes.X,
                    Height = 50,
                },
                new AverageHitError(hitEvents),
                calibrateFromLastPlayButton = new SettingsButton
                {
                    Text = BeatmapOffsetControlStrings.CalibrateUsingLastPlay,
                    Action = () =>
                    {
                        if (Current.Disabled)
                            return;

                        Current.Value = lastPlayBeatmapOffset - lastPlayMedian;
                        lastAppliedScore.Value = lastValidScore;
                    },
                },
                globalOffsetText = new LinkFlowContainer
                {
                    RelativeSizeAxes = Axes.X,
                    AutoSizeAxes = Axes.Y,
                }
            });

            if (settings != null)
            {
                globalOffsetText.AddText("You can also ");
                globalOffsetText.AddLink("adjust the global offset", () => settings.ShowAtControl<AudioOffsetAdjustControl>());
                globalOffsetText.AddText(" based off this play.");
            }
        }

        [Resolved]
        private SettingsOverlay? settings { get; set; }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            beatmapOffsetSubscription?.Dispose();
        }

        protected override void Update()
        {
            base.Update();

            bool allow = allowOffsetAdjust;

            if (calibrateFromLastPlayButton != null)
                calibrateFromLastPlayButton.Enabled.Value = allow && !Precision.AlmostEquals(lastPlayMedian, adjustmentSinceLastPlay, Current.Precision / 2);

            Current.Disabled = !allow;
        }

        private bool allowOffsetAdjust => player?.AllowCriticalSettingsAdjustment != false;

        public bool OnPressed(KeyBindingPressEvent<GlobalAction> e)
        {
            // To match stable, this should adjust by 5 ms, or 1 ms when holding alt.
            // But that is hard to make work with global actions due to the operating mode.
            // Let's use the more precise as a default for now.
            const double amount = 1;

            switch (e.Action)
            {
                case GlobalAction.IncreaseOffset:
                    if (!Current.Disabled)
                        Current.Value += amount;
                    return true;

                case GlobalAction.DecreaseOffset:
                    if (!Current.Disabled)
                        Current.Value -= amount;
                    return true;
            }

            return false;
        }

        public void OnReleased(KeyBindingReleaseEvent<GlobalAction> e)
        {
        }

        public static LocalisableString GetOffsetExplanatoryText(double offset)
        {
            string formatOffset = offset.ToStandardFormattedString(1);

            return formatOffset == "0"
                ? LocalisableString.Interpolate($@"{formatOffset} ms")
                : LocalisableString.Interpolate($@"{formatOffset} ms {getEarlyLateText(offset)}");

            LocalisableString getEarlyLateText(double value)
            {
                Debug.Assert(value != 0);

                return value > 0
                    ? BeatmapOffsetControlStrings.HitObjectsAppearEarlier
                    : BeatmapOffsetControlStrings.HitObjectsAppearLater;
            }
        }

        private partial class OffsetSliderBar : PlayerSliderBar<double>
        {
            protected override Drawable CreateControl() => new CustomSliderBar();

            protected partial class CustomSliderBar : SliderBar
            {
                public override LocalisableString TooltipText => GetOffsetExplanatoryText(Current.Value);
            }
        }
    }
}
