using F3M.Client.Business;
using F3M.Client.Models;
using F3M.Client.Services;
using F3M.Shared.Helpers;
using Microsoft.AspNetCore.Components;

namespace F3M.Client.Components
{
    public partial class InstallPreview
    {
        /// <summary>The same live list the file picker edits.</summary>
        [Parameter, EditorRequired]
        public List<FileEntry> Entries { get; set; } = [];

        // Reading an archive's index is the expensive part: do it once per file, not once per keystroke.
        private readonly Dictionary<FileEntry, ArchiveListing> _listings = [];
        private string _signature = string.Empty;
        private PreviewResult? _result;

        protected override void OnParametersSet()
        {
            var signature = string.Join('\n', Entries.Select(e => $"{e.OriginalName}|{e.InstallPath}|{e.Size}"));
            if (signature == _signature && _result is not null)
                return;
            _signature = signature;

            foreach (var removed in _listings.Keys.Where(k => !Entries.Contains(k)).ToList())
                _listings.Remove(removed);

            foreach (var entry in Entries)
            {
                if (InstallPaths.IsArchive(entry.OriginalName) && !_listings.ContainsKey(entry))
                    _listings[entry] = ArchiveInspector.List(entry.Bytes);
            }

            _result = InstallPreviewBuilder.Build(Entries, entry => _listings.GetValueOrDefault(entry));
        }
    }
}