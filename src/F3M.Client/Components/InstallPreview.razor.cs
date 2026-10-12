using F3M.Client.Business;
using F3M.Client.Models;
using F3M.Shared.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components
{
    public partial class InstallPreview
    {
        /// <summary>The same live list the file picker edits.</summary>
        [Parameter, EditorRequired]
        public List<FileEntry> Entries { get; set; } = [];

        /// <summary>The upload's optional groups, edited in place when files are marked in the tree; null hides the marking.</summary>
        [Parameter]
        public List<OptionalGroupDraft>? OptionalGroups { get; set; }

        /// <summary>The valid groups, normalised, which decide what the tree shows as optional.</summary>
        [Parameter]
        public IReadOnlyList<OptionalGroupDto> NormalizedGroups { get; set; } = [];

        /// <summary>Raised after the tree changed <see cref="OptionalGroups"/>.</summary>
        [Parameter]
        public EventCallback OnOptionalGroupsChanged { get; set; }

        // Each archive was listed once when it was picked (FileEntry.Listing); the tree is rebuilt only when a path changes.
        private string _signature = string.Empty;
        private PreviewResult? _result;
        private int _optionalCount;
        private OptionalMarking? _marking;

        protected override void OnParametersSet()
        {
            var signature = string.Join('\n', Entries.Select(e => $"{e.OriginalName}|{e.InstallPath}|{e.ArchiveRoot}|{e.Size}"));
            if (signature != _signature || _result is null)
            {
                _signature = signature;
                _result = InstallPreviewBuilder.Build(Entries);
            }

            // Cheap next to building the tree, and the groups change without the files changing.
            _optionalCount = InstallPreviewBuilder.MarkOptional(_result.Root, NormalizedGroups);
            _marking = OptionalGroups is null ? null : new OptionalMarking(this);
        }

        /// <summary>What the tree rows need to mark files optional: the groups, and the actions that change them.</summary>
        public sealed class OptionalMarking(InstallPreview owner)
        {
            public IReadOnlyList<string> GroupNames { get; } =
                [.. owner.OptionalGroups!.Select(g => g.Name.Trim()).Where(n => n.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

            /// <summary>True when the group names this entry exactly, so the tree can take it out again.</summary>
            public bool CanUnassign(PreviewNode node) => OptionalGroupChecks.IsNamedExactly(owner.OptionalGroups!, node.Path, !node.IsFile);

            public Task Assign(PreviewNode node, string? groupName)
            {
                OptionalGroupChecks.Assign(owner.OptionalGroups!, node.Path, !node.IsFile, groupName ?? OptionalGroupChecks.NewGroupName(owner.OptionalGroups!));
                return owner.OnOptionalGroupsChanged.InvokeAsync();
            }

            public Task Unassign(PreviewNode node)
            {
                OptionalGroupChecks.Unassign(owner.OptionalGroups!, node.Path, !node.IsFile);
                return owner.OnOptionalGroupsChanged.InvokeAsync();
            }
        }
    }
}
