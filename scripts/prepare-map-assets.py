"""Development-only Natural Earth conversion; Python standard library, no runtime fetches."""
import hashlib
import json
from pathlib import Path
from urllib.request import urlopen

REVISION = "f1890d9f152c896d250a77557a5751a93d494776"  # v5.1.2
BASE = f"https://raw.githubusercontent.com/nvkelso/natural-earth-vector/{REVISION}/geojson/"
HASHES = {
    "ne_110m_land": "9e0729ee253ca7d7a5c4ae9395fb1902264c5377c52e224d13dd85010e2835d9",
    "ne_10m_populated_places_simple": "fd3fa867a320cbd5c5b6bb5bc550afeec2939fb2cef688e508007282a55ac42f",
}
OUTPUT = Path(__file__).resolve().parents[1] / "assets" / "maps"


def source(name):
    with urlopen(BASE + name + ".geojson", timeout=60) as response:
        data = response.read(10_000_001)
    if len(data) > 10_000_000 or hashlib.sha256(data).hexdigest() != HASHES[name]:
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
    cities = []
    for feature in source("ne_10m_populated_places_simple"):
        props = feature["properties"]
        lon, lat = feature["geometry"]["coordinates"]
        cities.append({"id": str(props["ne_id"]), "name": props["name"], "country": props["adm0name"],
                       "latitude": round(lat, 4), "longitude": round(lon, 4)})
    write("land.json", rings)
    write("cities.json", sorted(cities, key=lambda city: (city["country"], city["name"], city["id"])))
    print(f"Wrote {len(rings)} land rings and {len(cities)} cities from Natural Earth v5.1.2 ({REVISION}).")


if __name__ == "__main__":
    main()
