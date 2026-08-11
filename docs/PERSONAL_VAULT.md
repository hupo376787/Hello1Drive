# OneDrive Personal Vault

Hello1Drive treats OneDrive Personal Vault as a protected OneDrive entry rather than a normal Graph folder.

- The item is identified from the `specialFolder` facet (`name == "vault"`) instead of its localized display name.
- Tapping/opening it launches the Graph-provided OneDrive `webUrl` (or the OneDrive web root as a fallback).
- Hello1Drive does not call `/items/{id}/children` for the vault, so a locked vault no longer produces a raw Graph `accessDenied` error.
- Personal Vault is excluded from the in-app Move/Copy destination browser because unlocking and protected-folder access must be completed in Microsoft's OneDrive experience.

Normal OneDrive folders are unaffected.

## Compatibility fallback

Some OneDrive Personal consumer responses do not reliably expose the Personal Vault `specialFolder` facet in ordinary children listings. Hello1Drive now explicitly requests `specialFolder`, also recognizes the common English and Chinese Personal Vault display names, and treats Graph `422 getChildrenOnNonFolder` as an official-OneDrive handoff rather than surfacing the raw Graph error.
