using F3M.Client.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components
{
    public partial class InstallPreviewNode
    {
        private const int MaxChildren = 200;
        private bool open;

        [Parameter, EditorRequired]
        public PreviewNode Node { get; set; } = default!;

        [Parameter]
        public int Depth { get; set; }

        protected override void OnInitialized() => open = Depth < 3;
    }
}