using F3M.Client.Business;
using F3M.Client.Models;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components
{
    public partial class InstallPreview
    {
        /// <summary>The same live list the file picker edits.</summary>
        [Parameter, EditorRequired]
        public List<FileEntry> Entries { get; set; } = [];

        // Each archive was listed once when it was picked (FileEntry.Listing); the tree is rebuilt only when a path changes.
        private string _signature = string.Empty;
        private PreviewResult? _result;

        protected override void OnParametersSet()
        {
            var signature = string.Join('\n', Entries.Select(e => $"{e.OriginalName}|{e.InstallPath}|{e.ArchiveRoot}|{e.Size}"));
            if (signature == _signature && _result is not null)
                return;
            _signature = signature;

            _result = InstallPreviewBuilder.Build(Entries);
        }
    }
}