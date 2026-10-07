"""Prints the mutation areas to run, as the JSON matrix of .github/workflows/mutation.yml.

Usage:
  select-areas.py               every area (scheduled and manual runs)
  select-areas.py CHANGED_FILES  the areas with a changed source file or Stryker configuration (pull requests);
                                 CHANGED_FILES lists one repository path per line (git diff --name-only).

The areas are declared in areas.json next to this script: `sources` is the regex of the source paths an area
mutates, `exclude` (optional) the paths it leaves out, and tests/stryker/<area>.yml its Stryker configuration.
"""
import json
import os
import re
import sys

here = os.path.dirname(os.path.abspath(__file__))
with open(os.path.join(here, "areas.json"), encoding="utf-8") as f:
    areas = json.load(f)


def touches(area, path):
    if path == f"tests/stryker/{area['area']}.yml":
        return True
    if not re.search(area["sources"], path):
        return False
    return not ("exclude" in area and re.search(area["exclude"], path))


if len(sys.argv) > 1:
    with open(sys.argv[1], encoding="utf-8") as f:
        changed = [line.strip() for line in f if line.strip()]
    areas = [a for a in areas if any(touches(a, path) for path in changed)]

print(json.dumps([{k: a[k] for k in ("area", "tests", "timeout")} for a in areas], separators=(",", ":")))
