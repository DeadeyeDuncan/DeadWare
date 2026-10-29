# Offline map assets

Source: [Natural Earth](https://www.naturalearthdata.com/), 1:110m land and country boundaries and 1:10m populated places (7,342 city choices), repository release **v5.1.2**, pinned revision `f1890d9f152c896d250a77557a5751a93d494776`.

These Natural Earth datasets are **public domain**: [upstream terms](https://www.naturalearthdata.com/about/terms-of-use/). Natural Earth is maintained by Nathaniel Vaughn Kelso, Tom Patterson and contributors. No endorsement is implied. Geographic locations are approximate; city choices do not determine device location.

Upstream URLs and SHA-256 of the original bytes:

- [ne_110m_land.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_110m_land.geojson): `9e0729ee253ca7d7a5c4ae9395fb1902264c5377c52e224d13dd85010e2835d9`
- [ne_110m_admin_0_boundary_lines_land.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_110m_admin_0_boundary_lines_land.geojson): `d42479fd79552cca4eec7f85fcdca717a790d29ff06be7676f1af0568c6d3f7c`
- [ne_10m_populated_places_simple.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_10m_populated_places_simple.geojson): `fd3fa867a320cbd5c5b6bb5bc550afeec2939fb2cef688e508007282a55ac42f`

Run `python scripts/prepare-map-assets.py` from any directory with Python 3. This development-only command fetches those pinned files and rejects unexpected hashes. Land retains exterior rings of the already generalized 110m data, rounds [longitude, latitude] pairs to two decimals and drops adjacent duplicate coordinates; inland holes are omitted because the renderer draws coastlines without polygon fills. Cities retain Natural Earth's stable `ne_id`, name and country, with coordinates rounded to four decimals and deterministic country/name/id ordering. Output is compact UTF-8 JSON embedded as WPF resources. Runtime code never fetches map assets or needs the working directory.

Country boundary LineString/MultiLineString features become 333 open paths, rounded to two decimal degrees with adjacent duplicates removed. Paths are never implicitly closed. This uses Natural Earth's default boundary representation; no custom territorial worldview or marine borders are added. The land and cities outputs are unchanged.

The renderer uses front-hemisphere coastlines, country borders, graticules and clipped great-circle traffic paths. It deliberately does not infer polygon fills or join separate boundary segments across the globe horizon or flat antimeridian. Border geometry is cached with the camera, viewport and main/widget presentation; the bundled data requires no runtime download.

State/province borders use 1:50m `ne_50m_admin_1_states_provinces_lines`, replacing its US/Canada features with 1:10m `ne_10m_admin_1_states_provinces_lines` features for the US, Canada and Mexico. Coverage elsewhere is Australia, Brazil, China, India, Indonesia, Russia and South Africa; this is not a global subdivision dataset. The same pinned revision and public-domain terms apply.

- [ne_50m_admin_1_states_provinces_lines.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_50m_admin_1_states_provinces_lines.geojson): `72cca93c850d412628a5da4bc5ebfe21ba4d376eb34611bde6b623ee73f0fdcf`
- [ne_10m_admin_1_states_provinces_lines.geojson](https://raw.githubusercontent.com/nvkelso/natural-earth-vector/f1890d9f152c896d250a77557a5751a93d494776/geojson/ne_10m_admin_1_states_provinces_lines.geojson): `1a1f30ccaaf4cc9c4bde34266f0b8cbb955d3a4cf254b756912255f2ec7c75b6`

The 708 selected features become 2,173 open paths with 32,584 coordinate pairs after rounding to .01 degree and dropping adjacent duplicates. No closing segments are added. Generation and runtime parsing enforce a 4,096-path / 32,768-coordinate limit; the detailed upstream download alone has a 24 MB input bound. Existing land, country and city asset contents remain unchanged. Subdivision geometry is cached only above 1.25x zoom, fades to full line opacity by 2.5x, and uses a thinner stroke than national boundaries. Both the main map and map-only widget use this layer; the widget's NA view is 2.5x.
