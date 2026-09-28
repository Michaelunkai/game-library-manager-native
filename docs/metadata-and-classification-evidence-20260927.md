# Metadata and 2D classification evidence

This change adds field-level evidence to the native metadata path and tightens the standalone 2D classifier. It records source identity and scope alongside values; it does not claim that every current library entry has complete evidence.

## Metadata fields

`metadata-evidence.json` in the profile cache is schema version 2. Each field stores its last accepted evidence separately from its last attempt and rejected attempts. A provider product ID must match the resolved identity map. Steam app details and HowLongToBeat URLs are checked against their exact product IDs, and every available field has a UTC observation time and freshness boundary. Artwork evidence also requires a decoded image, positive dimensions, and a cache path whose SHA-256 name matches the verified bytes.

The native refresh path records verified Steam artwork bytes and dimensions, published Windows storage requirements, and live HowLongToBeat main story, main plus extras, and completionist estimates with provider sample counts. It records Steam download size as unavailable because the Steam app details response used here does not provide a verified download-size value. It never substitutes the published disk requirement for download size.

HowLongToBeat results from the local cache can still appear in the ordinary game metadata, but they do not create fresh evidence because the helper does not return the cached observation timestamp. An unavailable attempt leaves a previously accepted value intact. For installed logical size, call `MetadataEvidence.RecordInstalledLogicalSize` with the exact installation path, logical bytes, and sampled file count. That method records decimal GB and binds the provider product identity to a normalized-path SHA-256. Unknown download size or a missing local measurement remains missing or unavailable rather than becoming zero.

## Gameplay plane classification

`GameClassification` applies the `2d` assignment only when at least two independent HTTPS origins of different kinds describe the same canonical product's primary gameplay plane as two dimensional. The evidence must be no older than 180 days and include a freshness boundary. Mixed gameplay, conflicting sources, missing identity, stale evidence, and unsupported source types stay in review.

Gameplay plane and presentation are separate fields. Corroborated 2D gameplay remains eligible when the sources describe 3D presentation, which covers plane-based 2.5D games. Mixed primary gameplay stays in review. The classifier protects `finished`, `not_for_me`, `meh`, and `hyperv` assignments, and callers must set `IsExplicitlyNonGame` for utilities or backup entries. A missing `2d` category is not created automatically.

Every applied batch writes evidence, its decisions, and an undo receipt in one local-catalog save. Undo restores only assignments still owned by that receipt, preserving later personal edits. The isolated tests use temporary profiles. Running the classifier against the live profile still requires the root task's immutable baseline and rollback evidence first.

## Integration requirements

The caller should build one `GameClassificationTarget` per verified canonical game, retain every linked source ID, and pass the effective assignment for each source. It should resolve `IsExplicitlyNonGame` from the game's established type and provide the reconciled category ID set. Evidence should come from publisher or developer sources, official stores, or platform metadata; each record needs the verified canonical product ID, actual source URL, exact primary gameplay plane, UTC observation time, and `FreshUntilUtc` within 180 days. Do not construct evidence from titles, tags, genres, artwork, or pixel style.

For installed-size coverage, call `RecordInstalledLogicalSize` only after the storage scanner has measured the exact installation. Download-size coverage needs an independent source that publishes an identity-matched download size; the Steam disk requirement does not satisfy it.

Current-library coverage remains unclaimed until the integrated caller supplies the live canonical game inventory, category assignments, and matched source evidence. Review entries should remain unchanged until that evidence exists.
