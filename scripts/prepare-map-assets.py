"""Development-only Natural Earth conversion; Python standard library, no runtime fetches."""
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

REVISION = "f1890d9f152c896d250a77557a5751a93d494776"  # v5.1.2
BASE = f"https://raw.githubusercontent.com/nvkelso/natural-earth-vector/{REVISION}/geojson/"
HASHES = {
    "ne_110m_land": "9e0729ee253ca7d7a5c4ae9395fb1902264c5377c52e224d13dd85010e2835d9",
    "ne_110m_admin_0_boundary_lines_land": "d42479fd79552cca4eec7f85fcdca717a790d29ff06be7676f1af0568c6d3f7c",
    "ne_10m_populated_places_simple": "fd3fa867a320cbd5c5b6bb5bc550afeec2939fb2cef688e508007282a55ac42f",
    "ne_50m_admin_1_states_provinces_lines": "72cca93c850d412628a5da4bc5ebfe21ba4d376eb34611bde6b623ee73f0fdcf",
    "ne_10m_admin_1_states_provinces_lines": "1a1f30ccaaf4cc9c4bde34266f0b8cbb955d3a4cf254b756912255f2ec7c75b6",
}
OUTPUT = Path(__file__).resolve().parents[1] / "assets" / "maps"


def source(name):
    limit = 24_000_000 if name == "ne_10m_admin_1_states_provinces_lines" else 10_000_000
    with urlopen(BASE + name + ".geojson", timeout=60) as response:
        data = response.read(limit + 1)
    if len(data) > limit or hashlib.sha256(data).hexdigest() != HASHES[name]:
        raise ValueError(f"Source size/hash mismatch: {name}")
    return json.loads(data)["features"]


def write(name, data):
    OUTPUT.mkdir(parents=True, exist_ok=True)
    (OUTPUT / name).write_text(json.dumps(data, ensure_ascii=False, separators=(",", ":")) + "\n", encoding="utf-8")


def main():
    rings = []
    for feature in source("ne_110m_land"):
        geometry = feature["geometry"]
        polygons = [geometry["coordinates"]] if geometry["type"] == "Polygon" else geometry["coordinates"]
        for polygon in polygons:
            # Keep exterior coastlines. 110m is already generalized; quantize to .01 degree.
            ring = []
            for longitude, latitude in polygon[0]:
                pair = [round(longitude, 2), round(latitude, 2)]
                if not ring or pair != ring[-1]:
                    ring.append(pair)
            if len(ring) >= 4:
                rings.append(ring)
    borders = []
    for feature in source("ne_110m_admin_0_boundary_lines_land"):
        geometry = feature["geometry"]
        if geometry["type"] not in ("LineString", "MultiLineString"):
            raise ValueError("Unexpected country boundary geometry")
        lines = [geometry["coordinates"]] if geometry["type"] == "LineString" else geometry["coordinates"]
        for line in lines:
            path = []
            for longitude, latitude in line:
                pair = [round(longitude, 2), round(latitude, 2)]
                if not path or pair != path[-1]:
                    path.append(pair)
            if len(path) >= 2:
                borders.append(path)
    # Retain the 50m coverage elsewhere; use detailed US/Canada/Mexico boundaries.
    features = [f for f in source("ne_50m_admin_1_states_provinces_lines") if f["properties"]["ADM0_A3"] not in ("USA", "CAN")]
    features += [f for f in source("ne_10m_admin_1_states_provinces_lines") if f["properties"]["ADM0_A3"] in ("USA", "CAN", "MEX")]
    states = []
    for feature in features:
        geometry = feature["geometry"]
        if geometry["type"] not in ("LineString", "MultiLineString"):
            raise ValueError("Unexpected state boundary geometry")
        lines = [geometry["coordinates"]] if geometry["type"] == "LineString" else geometry["coordinates"]
        for line in lines:
            path = []
            for longitude, latitude in line:
                pair = [round(longitude, 2), round(latitude, 2)]
                if not path or pair != path[-1]:
                    path.append(pair)
            if len(path) >= 2:
                states.append(path)
    if len(states) > 4096 or sum(map(len, states)) > 32768:
        raise ValueError("State boundaries exceed runtime geometry limits")
    cities = []
    for feature in source("ne_10m_populated_places_simple"):
        props = feature["properties"]
        lon, lat = feature["geometry"]["coordinates"]
        cities.append({"id": str(props["ne_id"]), "name": props["name"], "country": props["adm0name"],
                       "latitude": round(lat, 4), "longitude": round(lon, 4)})
    write("land.json", rings)
    write("borders.json", borders)
    write("states.json", states)
    write("cities.json", sorted(cities, key=lambda city: (city["country"], city["name"], city["id"])))
    print(f"Wrote {len(rings)} land rings, {len(borders)} country boundaries, {len(states)} state/province paths and {len(cities)} cities from Natural Earth v5.1.2 ({REVISION}).")


if __name__ == "__main__":
    main()
