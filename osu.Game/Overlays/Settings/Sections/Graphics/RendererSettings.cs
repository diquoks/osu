// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics;
using osu.Framework.Localisation;
using osu.Framework.Platform;
using osu.Game.Configuration;
using osu.Game.Graphics.UserInterfaceV2;
using osu.Game.Localisation;
using osu.Game.Overlays.Dialog;

namespace osu.Game.Overlays.Settings.Sections.Graphics
{
    public partial class RendererSettings : SettingsSubsection
    {
        protected override LocalisableString Header => GraphicsSettingsStrings.RendererHeader;

        private bool automaticRendererInUse;

        [BackgroundDependencyLoader]
        private void load(FrameworkConfigManager config, OsuConfigManager osuConfig, IDialogOverlay? dialogOverlay, OsuGame? game, GameHost host)
        {
            var renderer = config.GetBindable<RendererType>(FrameworkSetting.Renderer);
            automaticRendererInUse = renderer.Value == RendererType.Automatic;

            IEnumerable<RendererType> availableRenderers = host.GetPreferredRenderersForCurrentPlatform().Order();

            // Vulkan renderers are pretty broken to the point it may result in a startup crash at worst.
            // If a user isn't already using it let's hide it until we can fix.
            if (renderer.Value != RendererType.Deferred_Vulkan)
                availableRenderers = availableRenderers.Where(t => t != RendererType.Deferred_Vulkan);
            if (renderer.Value != RendererType.Vulkan)
                availableRenderers = availableRenderers.Where(t => t != RendererType.Vulkan);

            Children = new Drawable[]
            {
                new SettingsItemV2(new RendererDropdown
                {
                    Caption = GraphicsSettingsStrings.Renderer,
                    Current = renderer,
                    Items = availableRenderers,
                })
                {
                    Keywords = new[] { @"compatibility", @"directx" },
                },
                // TODO: this needs to be a custom dropdown at some point
                new SettingsItemV2(new FrameSyncDropdown
                {
                    Caption = GraphicsSettingsStrings.FrameLimiter,
                    Current = config.GetBindable<FrameSync>(FrameworkSetting.FrameSync),
                })
                {
                    Keywords = new[] { @"fps", @"framerate" },
                },
                new SettingsItemV2(new ExecutionModeDropdown
                {
                    Caption = GraphicsSettingsStrings.ThreadingMode,
                    Current = config.GetBindable<ExecutionMode>(FrameworkSetting.ExecutionMode)
                }),
                new SettingsItemV2(new FormCheckBox
                {
                    Caption = GraphicsSettingsStrings.ShowFPS,
                    Current = osuConfig.GetBindable<bool>(OsuSetting.ShowFpsDisplay),
                })
                {
                    Keywords = new[] { @"framerate", @"counter" },
                },
            };

            renderer.BindValueChanged(r =>
            {
                if (r.NewValue == host.ResolvedRenderer)
                    return;

                // Need to check startup renderer for the "automatic" case, as ResolvedRenderer above will track the final resolved renderer instead.
                if (r.NewValue == RendererType.Automatic && automaticRendererInUse)
                    return;

                if (game?.RestartAppWhenExited() == true)
                {
                    game.AttemptExit();
                }
                else
                {
                    dialogOverlay?.Push(new ConfirmDialog(GraphicsSettingsStrings.ChangeRendererConfirmation, () => game?.AttemptExit(), () =>
                    {
                        renderer.Value = automaticRendererInUse ? RendererType.Automatic : host.ResolvedRenderer;
                    }));
                }
            });
        }

        private partial class RendererDropdown : FormEnumDropdown<RendererType>
        {
            private RendererType hostResolvedRenderer;
            private bool automaticRendererInUse;

            [BackgroundDependencyLoader]
            private void load(FrameworkConfigManager config, GameHost host)
            {
                var renderer = config.GetBindable<RendererType>(FrameworkSetting.Renderer);
                automaticRendererInUse = renderer.Value == RendererType.Automatic;
                hostResolvedRenderer = host.ResolvedRenderer;
            }

            protected override LocalisableString GenerateItemText(RendererType item)
            {
                switch (item)
                {
                    case RendererType.Automatic:
                        // `hostResolvedRenderer != RendererType.Automatic` needed here to prevent recursion (I don't think it's possible at all, but just to be sure)
                        if (automaticRendererInUse && hostResolvedRenderer != RendererType.Automatic)
                            return GraphicsSettingsStrings.AutomaticRendererInUse(GenerateItemText(hostResolvedRenderer));

                        return GraphicsSettingsStrings.AutomaticRenderer;

                    case RendererType.Deferred_Metal:
                        return GraphicsSettingsStrings.ExperimentalRenderer(GenerateItemText(RendererType.Metal));

                    case RendererType.Deferred_Vulkan:
                        return GraphicsSettingsStrings.ExperimentalRenderer(GenerateItemText(RendererType.Vulkan));

                    case RendererType.Deferred_Direct3D11:
                        return GraphicsSettingsStrings.ExperimentalRenderer(GenerateItemText(RendererType.Direct3D11));

                    case RendererType.Deferred_OpenGL:
                        return GraphicsSettingsStrings.ExperimentalRenderer(GenerateItemText(RendererType.OpenGL));

                    default:
                        return base.GenerateItemText(item);
                }
            }
        }

        private partial class FrameSyncDropdown : FormEnumDropdown<FrameSync>
        {
            protected override LocalisableString GenerateItemText(FrameSync item)
            {
                switch (item)
                {
                    case FrameSync.VSync:
                        return GraphicsSettingsStrings.VSyncFrameLimiter;

                    case FrameSync.Limit2x:
                        return GraphicsSettingsStrings.RefreshRateMultiplierFrameLimiter(2);

                    case FrameSync.Limit4x:
                        return GraphicsSettingsStrings.RefreshRateMultiplierFrameLimiter(4);

                    case FrameSync.Limit8x:
                        return GraphicsSettingsStrings.RefreshRateMultiplierFrameLimiter(8);

                    case FrameSync.Unlimited:
                        return GraphicsSettingsStrings.UnlimitedFrameLimiter;

                    default:
                        return base.GenerateItemText(item);
                }
            }
        }

        private partial class ExecutionModeDropdown : FormEnumDropdown<ExecutionMode>
        {
            protected override LocalisableString GenerateItemText(ExecutionMode item)
            {
                switch (item)
                {
                    case ExecutionMode.SingleThread:
                        return GraphicsSettingsStrings.SingleThreadThreadingMode;

                    case ExecutionMode.MultiThreaded:
                        return GraphicsSettingsStrings.MultithreadedThreadingMode;

                    default:
                        return base.GenerateItemText(item);
                }
            }
        }
    }
}
