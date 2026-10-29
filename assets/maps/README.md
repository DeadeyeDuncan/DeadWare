# Offline map assets

Source: [Natural Earth](https://www.naturalearthdata.com/), 1:110m land and 1:10m populated places (7,342 city choices), release **v5.1.2**, pinned repository revision `f1890d9f152c896d250a77557a5751a93d494776`.

These Natural Earth datasets are **public domain**: [upstream terms](https://www.naturalearthdata.com/about/terms-of-use/). Natural Earth is maintained by Nathaniel Vaughn Kelso, Tom Patterson and contributors. No endorsement is implied. Geographic locations are approximate; city choices do not determine device location.

Upstream URLs and SHA-256 of the original bytes:

- [ne_110m_land.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_110m_land.geojson): `9e0729ee253ca7d7a5c4ae9395fb1902264c5377c52e224d13dd85010e2835d9`
- [ne_10m_populated_places_simple.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_10m_populated_places_simple.geojson): `fd3fa867a320cbd5c5b6bb5bc550afeec2939fb2cef688e508007282a55ac42f`

Run `python scripts/prepare-map-assets.py` from any directory with Python 3. This development-only command fetches those pinned files and rejects unexpected hashes. Land retains exterior rings of the already generalized 110m data, rounds [longitude, latitude] pairs to two decimals and drops adjacent duplicate coordinates; inland holes are omitted because the renderer draws coastlines without polygon fills. Cities retain Natural Earth's stable `ne_id`, name and country, with coordinates rounded to four decimals and deterministic country/name/id ordering. Output is compact UTF-8 JSON embedded as WPF resources. Runtime code never fetches map assets or needs the working directory.

The renderer uses front-hemisphere coastlines, graticules and clipped great-circle traffic paths. It deliberately does not infer land polygon fills across the globe horizon or flat antimeridian.
