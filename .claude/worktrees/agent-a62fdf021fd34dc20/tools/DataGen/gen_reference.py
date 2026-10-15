"""Generates the committed, fictional reference data shared by the app seed and the mock services.
Deterministic (fixed random seed). Run: python3 -I gen_reference.py <repo-root>"""
import json, random, sys, pathlib

root = pathlib.Path(sys.argv[1]) / "data" / "reference"
root.mkdir(parents=True, exist_ok=True)
rng = random.Random(20261015)

cemeteries = [
    {"code": "RMP", "name": "Riverside Memorial Park", "address": "100 Willow Bend Road, Fairhaven, Practice State 00011", "timeZoneId": "America/New_York"},
    {"code": "CHG", "name": "Cedar Hollow Memorial Grounds", "address": "42 Lantern Hill Drive, Brookmere, Practice State 00022", "timeZoneId": "America/Chicago"},
    {"code": "MVR", "name": "Mesa Vista Remembrance Gardens", "address": "7 Juniper Mesa Way, Sunridge, Practice State 00033", "timeZoneId": "America/Denver"},
]

venue_kinds = [
    ("CHAPEL", "Chapel of Light", 80, 30, 30),
    ("LAWN", "Great Lawn", 300, 45, 45),
    ("PAVILION", "Memorial Pavilion", 150, 30, 30),
    ("GARDEN", "Quiet Garden", 40, 15, 15),
]

weekday = {"opens": "08:00", "closes": "18:00"}
saturday = {"opens": "09:00", "closes": "16:00"}
venues = []
for c in cemeteries:
    for i, (kind, name, cap, setup, teardown) in enumerate(venue_kinds):
        code = f"{c['code']}-{kind}"
        hours = [{"day": d, **weekday} for d in ["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"]] + [{"day": "Saturday", **saturday}]
        if kind == "LAWN":
            hours.append({"day": "Sunday", "opens": "10:00", "closes": "15:00"})
        venues.append({
            "code": code,
            "cemeteryCode": c["code"],
            "name": f"{c['name'].split()[0]} {name}",
            "address": f"{10 + i * 7} {name} Path, {c['address'].split(', ')[1]}, Practice State",
            "capacity": cap,
            "setupBufferMinutes": setup,
            "teardownBufferMinutes": teardown,
            "openingHours": hours,
            "allowedCategories": ["MEMORIAL", "COMMEMORATION", "WREATH", "FLYOVER"] if kind == "LAWN" else ["MEMORIAL", "COMMEMORATION", "WREATH", "CONCERT"] if kind == "PAVILION" else ["MEMORIAL", "WREATH"],
        })

resources = [
    ("CHAIRS", "Folding chairs", "each", 400),
    ("TENTS", "Shade tents", "each", 12),
    ("PARKING", "Parking attendants", "person", 8),
    ("AV", "AV kits", "kit", 4),
    ("PODIUM", "Podiums", "each", 3),
    ("FLAGS", "Ceremonial flag sets", "set", 10),
    ("SHUTTLE", "Accessible shuttle carts", "cart", 3),
    ("BARRIERS", "Crowd barriers", "section", 40),
]

(root / "sites.json").write_text(json.dumps({
    "cemeteries": cemeteries,
    "venues": venues,
    "resources": [{"code": c, "name": n, "unit": u, "quantityPerCemetery": q} for c, n, u, q in resources],
}, indent=2) + "\n")

first = ["Avery", "Jordan", "Quinn", "Rowan", "Harper", "Elliot", "Sasha", "Morgan", "Reese", "Tatum", "Emerson", "Kendall", "Parker", "Sloane", "Dakota", "Marlowe", "Remy", "Hollis", "Briar", "Linden"]
last = ["Practice-Alder", "Practice-Birch", "Practice-Cove", "Practice-Dunmore", "Practice-Everly", "Practice-Fenwick", "Practice-Galloway", "Practice-Hearth", "Practice-Ingram", "Practice-Juniper", "Practice-Keswick", "Practice-Larkin", "Practice-Moss"]
orgs = ["Fictional Veterans Fellowship Post 12", "Sample Honor Society", "Example Memorial Choir", "Practice Heritage Club", "Mock Remembrance League", ""]
applicants = []
for n in range(1, 101):
    status = "Valid"
    if n % 11 == 0:
        status = "Flagged"
    elif n % 7 == 0:
        status = "Unknown"
    name = f"{first[n % len(first)]} {last[(n * 3) % len(last)]}"
    applicants.append({
        "referenceId": f"REF-{10000 + n}",
        "matchedName": name,
        "eligibilityStatus": status,
        "organization": orgs[n % len(orgs)],
        "email": f"applicant{n:03d}@example.test",
        "phone": f"+1-555-01{n:02d}" if n < 100 else "+1-555-0199",
    })
(root / "registry.json").write_text(json.dumps({
    "note": "Fictional applicant registry. References REF-10101 to REF-10120 deliberately do not exist (404).",
    "applicants": applicants,
}, indent=2) + "\n")

# Regional calendar events, relative to the mock's current UTC date so they stay near the seed anchor (A-29).
events = []
lawn_like = [v["code"] for v in venues]
for n in range(1, 41):
    venue = lawn_like[(n * 5) % len(lawn_like)]
    day = (n % 21) - 3  # from 3 days ago to 17 days ahead
    hour = 9 + (n % 6)
    events.append({
        "externalId": f"EXT-{n:04d}",
        "venueCode": venue,
        "title": rng.choice(["Regional color guard rehearsal", "County remembrance walk", "Partner choir practice", "Grounds maintenance window", "School heritage visit"]),
        "dayOffset": day,
        "localStart": f"{hour:02d}:00",
        "durationMinutes": rng.choice([60, 90, 120]),
    })
(root / "calendar-events.json").write_text(json.dumps({
    "note": "Fictional regional calendar. dayOffset is relative to the current UTC date; times are venue-local.",
    "events": events,
}, indent=2) + "\n")

forecast = {
    "note": "Fictional weather. Each venue has a baseline; severeDays lists day offsets (from today, venue-local) with a Severe advisory.",
    "venues": [{"venueCode": v["code"], "baseTemperatureC": 14 + (i % 9), "baseWindKph": 8 + (i * 3) % 20, "severeDays": [1] if v["code"] == "RMP-LAWN" else [3] if v["code"] == "MVR-PAVILION" else [], "advisoryDays": [2] if i % 4 == 1 else []}
                for i, v in enumerate(venues)],
}
(root / "weather.json").write_text(json.dumps(forecast, indent=2) + "\n")

geo_base = {"RMP": (39.10, -76.20), "CHG": (41.80, -88.10), "MVR": (35.10, -106.60)}
geocode = []
for i, v in enumerate(venues):
    lat, lon = geo_base[v["cemeteryCode"]]
    lat += (i % 4) * 0.004
    lon += (i % 4) * 0.005
    candidates = [{"label": f"{v['name']}, {v['address']}", "latitude": round(lat, 5), "longitude": round(lon, 5), "confidence": 0.94}]
    if v["code"] == "CHG-GARDEN":  # ambiguous: two candidates tie
        candidates = [
            {"label": f"{v['name']} (north entrance), Brookmere", "latitude": round(lat, 5), "longitude": round(lon, 5), "confidence": 0.81},
            {"label": f"{v['name']} (south entrance), Brookmere", "latitude": round(lat - 0.003, 5), "longitude": round(lon + 0.002, 5), "confidence": 0.81},
        ]
    elif v["code"] == "MVR-GARDEN":  # no match
        candidates = []
    else:
        candidates.append({"label": f"{v['address'].split(',')[1].strip()} town centre", "latitude": round(lat + 0.02, 5), "longitude": round(lon - 0.02, 5), "confidence": 0.42})
    geocode.append({"query": v["address"], "venueCode": v["code"], "candidates": candidates})
(root / "geocode.json").write_text(json.dumps({
    "note": "Fictional geocoder answers keyed by exact address. Unknown queries return an empty list.",
    "entries": geocode,
}, indent=2) + "\n")

print("written", [p.name for p in root.iterdir()])
