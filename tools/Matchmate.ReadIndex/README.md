# Prepared football read index

This local maintenance command derives immutable catalog, nontracking context and minute seek pages from an existing durable journal. It never reimports sources, removes versions or rewrites payload/metadata chunks. Preparation adds derived blobs and atomically replaces only the optional `derivedReadView` reference in each dataset root. The source fence hashes every original root field. All five existing streams remain available.

Build with the repository RC2 SDK and existing dependencies. Run the built DLL with `--store <existing-directory>` and one final argument:

- `--inspect`: root descriptors and source fence hashes only; no writes.
- `--prepare`: explicitly prepare missing read views outside the viewer request path. Review the command against a copied store before applying it to a live local store. Existing valid prepared catalogs take the fast path without journal recovery.
- `--catalog`: first and repeated production catalog reads, counters and process measurements; no writes.
- `<query.json>`: first and repeated production query reads, without a preceding catalog read; no writes. Use the normal `FootballQuery` JSON contract.

Each invocation is a fresh process. Times exclude process launch, but do not imply cold OS file caches. Output contains dataset/query identifiers and bounded structural counts, never credentials. The utility has no network or model client.

Catalog/seek reads validate their prepared hash/length/path, source fence, stream identity, manifest, head and selected payload membership. They do not rescan archival journal pages. Explicit `ReadAsync` recovery/audit still validates those historical pages. Missing/corrupt/unbound prepared views fail safely; the viewer does not rebuild them. Ingest prepares updated read pages before the same source-membership CAS. Failed CAS can leave unreferenced immutable blobs, which are never served.
