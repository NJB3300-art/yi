using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Storage;

namespace Facet3D
{
    public sealed class StorageLocation
    {
        public string Name;
        public StorageFolder Folder;
    }

    public sealed class FolderListing
    {
        public List<StorageFolder> Folders = new List<StorageFolder>();
        public List<StorageFile> Files = new List<StorageFile>();
    }

    /// <summary>USB drives (removable storage) plus the app's own storage folder.</summary>
    public static class UsbStorage
    {
        public static async Task<List<StorageLocation>> GetLocationsAsync()
        {
            var list = new List<StorageLocation>();
            try
            {
                var drives = await KnownFolders.RemovableDevices.GetFoldersAsync();
                foreach (var d in drives) list.Add(new StorageLocation { Name = "USB: " + (string.IsNullOrEmpty(d.DisplayName) ? d.Name : d.DisplayName), Folder = d });
            }
            catch (Exception)
            {
                // No removable storage access (capability missing or no drive) - fall through to app storage.
            }
            list.Add(new StorageLocation { Name = "App storage (internal)", Folder = ApplicationData.Current.LocalFolder });
            return list;
        }

        public static async Task<FolderListing> ListAsync(StorageFolder folder, IEnumerable<string> extensions)
        {
            var result = new FolderListing();
            var exts = extensions == null ? null : new HashSet<string>(extensions.Select(e => e.ToLowerInvariant()));
            var folders = await folder.GetFoldersAsync();
            result.Folders.AddRange(folders.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase));
            var files = await folder.GetFilesAsync();
            foreach (var f in files.OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
                if (exts == null || exts.Contains(Path.GetExtension(f.Name).ToLowerInvariant())) result.Files.Add(f);
            return result;
        }

        public static async Task<byte[]> ReadAllAsync(StorageFile file)
        {
            using (var s = await file.OpenStreamForReadAsync())
            using (var ms = new MemoryStream())
            {
                await s.CopyToAsync(ms);
                return ms.ToArray();
            }
        }

        /// <summary>Synchronous sibling opener, for use from a background thread during import.</summary>
        public static Stream OpenSibling(StorageFolder folder, string relative)
        {
            try
            {
                string rel = relative.Replace('/', '\\').TrimStart('\\');
                if (rel.StartsWith(".\\")) rel = rel.Substring(2);
                var item = folder.TryGetItemAsync(rel).AsTask().GetAwaiter().GetResult() as StorageFile;
                if (item == null) item = folder.TryGetItemAsync(Path.GetFileName(rel)).AsTask().GetAwaiter().GetResult() as StorageFile;
                if (item == null) return null;
                return item.OpenStreamForReadAsync().GetAwaiter().GetResult();
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Synchronous file creator, for use from a background thread during export.</summary>
        public static Stream CreateFile(StorageFolder folder, string name)
        {
            var file = folder.CreateFileAsync(name, CreationCollisionOption.ReplaceExisting).AsTask().GetAwaiter().GetResult();
            return file.OpenStreamForWriteAsync().GetAwaiter().GetResult();
        }
    }
}
