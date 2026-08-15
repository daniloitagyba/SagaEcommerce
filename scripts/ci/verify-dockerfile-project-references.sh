#!/usr/bin/env bash
# Guards against the exact class of break this repo already hit once:
#
#   cfa7117 fix(ci): include shared HTTP clients in container builds
#
# BuildingBlocks.HttpClients was added to the solution and wired into every
# .csproj that needed it, dotnet build (CI's own "build and test" job) went
# green - and the image build still broke, because nothing copies a project
# into a Docker build context just because a .csproj references it. Each
# service's Dockerfile hand-lists every BuildingBlocks.* project it needs,
# twice (once for the .csproj restore-cache layer, once for the sources),
# and there is nothing structural stopping the next new project from
# repeating exactly this gap.
#
# This resolves each service's full *transitive* ProjectReference closure
# from its own .csproj (transitive, not just direct - e.g. Orders.Api
# doesn't reference BuildingBlocks.Resilience directly, only reaches it
# through BuildingBlocks.Persistence, but still needs it physically present
# in the build context for restore to succeed) and asserts every project in
# that closure has a matching COPY line in the paired Dockerfile.
set -euo pipefail

cd "$(dirname "$0")/../.."

python3 - <<'PYEOF'
import glob
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

SRC = Path("apps/src")

# service -> (csproj path, Dockerfile path)
SERVICES = {
    "orders-api": (SRC / "Orders.Api" / "Orders.Api.csproj", SRC / "Orders.Api" / "Dockerfile"),
    "orders-worker": (SRC / "Orders.Worker" / "Orders.Worker.csproj", SRC / "Orders.Worker" / "Dockerfile"),
    "payments-service": (SRC / "Payments.Service" / "Payments.Service.csproj", SRC / "Payments.Service" / "Dockerfile"),
    "catalog-service": (SRC / "Catalog.Service" / "Catalog.Service.csproj", SRC / "Catalog.Service" / "Dockerfile"),
    "inventory-service": (SRC / "Inventory.Service" / "Inventory.Service.csproj", SRC / "Inventory.Service" / "Dockerfile"),
    "cart-service": (SRC / "Cart.Service" / "Cart.Service.csproj", SRC / "Cart.Service" / "Dockerfile"),
    "storefront-service": (SRC / "Storefront.Service" / "Storefront.Service.csproj", SRC / "Storefront.Service" / "Dockerfile"),
}


def direct_references(csproj_path: Path) -> list[Path]:
    root = ET.parse(csproj_path).getroot()
    references = []
    for node in root.iter("ProjectReference"):
        include = node.attrib.get("Include")
        if not include:
            continue
        relative = include.replace("\\", "/")
        references.append((csproj_path.parent / relative).resolve())
    return references


def transitive_closure(csproj_path: Path) -> set[Path]:
    seen: set[Path] = set()
    stack = [csproj_path.resolve()]
    while stack:
        current = stack.pop()
        for reference in direct_references(current):
            if reference not in seen:
                seen.add(reference)
                stack.append(reference)
    return seen


failures = []
for service, (csproj_path, dockerfile_path) in SERVICES.items():
    dockerfile_text = dockerfile_path.read_text()
    required_projects = {path.parent.name for path in transitive_closure(csproj_path)}

    for project_name in sorted(required_projects):
        # Either the restore-cache .csproj-only COPY or the full-source COPY
        # names the project's directory - one match is enough to prove the
        # Dockerfile knows about it at all.
        pattern = re.compile(rf"COPY apps/src/{re.escape(project_name)}/")
        if not pattern.search(dockerfile_text):
            failures.append((service, project_name))

if failures:
    print("verify-dockerfile-project-references: missing COPY line(s) for a ProjectReference:")
    print()
    for service, project_name in failures:
        print(f"  {service}'s Dockerfile has no COPY for {project_name}, which its .csproj graph depends on")
    print()
    print(
        "Add the matching COPY (csproj-only restore-cache line, then full-source "
        "line) to that service's Dockerfile - see any existing BuildingBlocks.* "
        "block in the same file for the exact two-line shape."
    )
    sys.exit(1)

print("verify-dockerfile-project-references: OK - every service's Dockerfile copies its full transitive ProjectReference closure")
PYEOF
