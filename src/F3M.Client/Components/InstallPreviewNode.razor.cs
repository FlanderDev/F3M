using F3M.Client.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components
{
    public partial class InstallPreviewNode
    {
        private const int MaxChildren = 200;
        private bool open;
        private bool menuOpen;

        [Parameter, EditorRequired]
        public PreviewNode Node { get; set; } = default!;

        [Parameter]
        public int Depth { get; set; }

        /// <summary>The optional group of the folder this row is in, if all of it is in one.</summary>
        [Parameter]
        public string? ParentGroup { get; set; }

        /// <summary>The optional-group actions; null when the preview is shown without them.</summary>
        [CascadingParameter]
        public InstallPreview.OptionalMarking? Marking { get; set; }

        protected override void OnInitialized() => open = Depth < 3;

        private async Task AssignAsync(string? groupName)
        {
            menuOpen = false;
            if (Marking is not null)
                await Marking.Assign(Node, groupName);
        }
    }
}
