from pathlib import Path

path = Path('src/Hello1Drive.Core/ViewModels/MainViewModel.cs')
text = path.read_text(encoding='utf-8')

old = '''        var refreshesPresentedFolder = forceRemote &&\n            string.Equals(_presentedFolderCacheKey, cacheKey, StringComparison.Ordinal);'''
new = '''        var refreshesPresentedFolder = forceRemote &&\n            string.Equals(_presentedFolderCacheKey, cacheKey, StringComparison.Ordinal) &&\n            (_allItems.Count > 0 || MobileItems.Count > 0 || _folderCache.ContainsKey(cacheKey));'''
if text.count(old) != 1:
    raise RuntimeError(f'presented-folder guard matches: {text.count(old)}')
text = text.replace(old, new, 1)

old = '''    private void InvalidateFolderCache(string? folderId)\n    {\n        var key = FolderCacheKey(folderId);\n        if (_folderCache.Remove(key, out var entry))\n            DisposeItemThumbnails(entry.Items);\n    }'''
new = '''    private void InvalidateFolderCache(string? folderId)\n    {\n        var key = FolderCacheKey(folderId);\n        if (!_folderCache.Remove(key, out var entry))\n            return;\n\n        // Invalidating the folder currently on screen is a network/cache coherency operation, not\n        // a request to tear down its visual state. The rendered _allItems still own these models and\n        // their decoded thumbnails until the incremental cloud result arrives.\n        if (!string.Equals(key, _presentedFolderCacheKey, StringComparison.Ordinal))\n            DisposeItemThumbnails(entry.Items);\n    }'''
if text.count(old) != 1:
    raise RuntimeError(f'invalidate cache matches: {text.count(old)}')
text = text.replace(old, new, 1)

old = '''        _folderCache.Clear();\n        _allItems.Clear();'''
new = '''        _folderCache.Clear();\n        _presentedFolderCacheKey = "__ROOT__";\n        _allItems.Clear();'''
if text.count(old) != 1:
    raise RuntimeError(f'clear cache matches: {text.count(old)}')
text = text.replace(old, new, 1)

path.write_text(text, encoding='utf-8', newline='\n')
print('Incremental refresh edge cases patched.')
