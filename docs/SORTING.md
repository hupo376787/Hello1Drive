# Folder sorting

Hello1Drive has a two-level sort policy:

1. **Settings default sort**: System default, Date/Name/Size ascending or descending. Changing this setting clears every remembered per-folder sort rule, so the new default takes effect across OneDrive.
2. **Per-folder override**: after the global default is set, any folder can choose its own Date/Name/Size ascending or descending rule. The sort menu exposes one **System default** entry; choosing it removes that folder's override and returns the folder to the Settings default.

The explicit Graph-original order remains an internal compatibility state. It is used when a backend cannot honor a requested server-side order, but it is no longer exposed as a second user-visible “default” item.

- Name: Graph `$orderby=name asc|desc`.
- Size: Graph `$orderby=size asc|desc`.
- Modified time: Graph `$orderby=lastModifiedDateTime asc|desc`.

Type sorting is intentionally not exposed. OneDrive's collection `$orderby` supports `name`, `size`, and `lastModifiedDateTime`; keeping sorting server-side ensures the entire folder remains globally ordered across Graph paging.

The toolbar sort button, the left-most sort button in Details headers, sortable column headers, and the background context menu all use the same remembered rule. Older remembered Type-sort rules are discarded automatically.

## Size-order compatibility

OneDrive documents `size` as orderable, but some consumer backends return 501 with the internal `SMTotalFileStreamSize` field. Hello1Drive sends `Prefer: HonorNonIndexedQueriesWarningMayFailRandomly` for size-ordered pages. If that backend still rejects the request, that folder receives an internal Graph-original-order fallback and is retried in the API's original order instead of surfacing a fatal error. This also prevents a global Size default from repeatedly retrying a backend that cannot support it.
