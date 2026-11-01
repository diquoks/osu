// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using osu.Framework.Graphics;
using osu.Game.Graphics.Containers;

namespace osu.Game.Overlays.BeatmapSet
{
    public partial class MetadataSectionPackTags : MetadataSection<string[]>
    {
        public override string[] Metadata
        {
            set
            {
                if (value.Length == 0)
                {
                    this.FadeOut(TRANSITION_DURATION);
                    return;
                }

                base.Metadata = value;
            }
        }

        public MetadataSectionPackTags()
            : base(MetadataType.PackTags)
        {
        }

        protected override void AddMetadata(string[]? tags, LinkFlowContainer loaded)
        {
            if (tags == null)
                return;

            for (int i = 0; i < tags.Length - 1; i++)
            {
                string tag = tags[i];

                loaded.AddLink(tag, $@"/beatmaps/packs/{tag}");

                if (i != tags.Length - 1)
                    loaded.AddText(" ");
            }
        }
    }
}
