import hashlib
import importlib
import json
from pathlib import Path
import sys
import types

sys.dont_write_bytecode = True
sdk = Path(sys.argv[1]).resolve() / "hd2_system"
package = types.ModuleType("ias_reference")
package.__path__ = [str(sdk)]
sys.modules[package.__name__] = package
free = importlib.import_module("ias_reference.free_runtime_manifest")
isolation = importlib.import_module("ias_reference.resource_isolation")
archive = "0123456789abcdef"

def option(identity, name, order, slot, pairs):
    return dict(id=identity, name=name, order=order, part_slot=slot, object_names=[identity],
                routes=[dict(native_unit_id=native, difference_unit_id=alias) for native, alias in pairs])

plan = dict(schema="HD2IndependentPackagePlan1", project_id="source-fixture", project_name="fixture",
            archive_name=archive, content_domain="BODY", resource_target_policy="PROJECT_ARCHIVE_ALIASES",
            native_saves=[dict(part_slot="Hips", native_unit_id=0x301, base_unit_id=0x3301, object_names=["base"])],
            difference_manifest=dict(difference_logic="FREE", base_group=dict(name="base"), free_parts=[
                dict(part_slot="Torso", name="上衣", options=[
                    option("on", "显示", 0, "Torso", [(0x102, 0x1102), (0x101, 0x1101)]),
                    option("off", "隐藏", 1, "Torso", [(0x101, 0x1201), (0x102, 0x1202)])]),
                dict(part_slot="LeftArm", name="袖子", options=[
                    option("long", "长袖", 0, "LeftArm", [(0x201, 0x2101)]),
                    option("short", "短袖", 1, "LeftArm", [(0x201, 0x2201)])])]))
catalog = dict(targets={archive: dict(domain="BODY", equipment=[
    dict(key="02030405", unit_ids=["101", "102", "201", "301"]),
    dict(key="01020304", unit_ids=["101", "102", "201", "301"])])})
profiles = [0x1101, 0x2201, 0x3301]
difference = free.build_free_runtime_manifest(plan, profiles, catalog)
owned = isolation.binding_for_plan(plan, profiles, catalog)
output = Path(__file__).parent / "Fixtures"
output.mkdir(exist_ok=True)
(output / "difference-reference.ini").write_bytes(difference["text"].encode("utf-8"))
(output / "isolation-reference.ini").write_bytes(owned["text"].encode("utf-8"))
metadata = dict(plan=plan, catalog=catalog, profiles=profiles,
                reference_source="https://github.com/Estecsky/HD2SDK-AQ-Edition",
                source_sha256={name: hashlib.sha256((sdk / name).read_bytes()).hexdigest()
                               for name in ("free_runtime_manifest.py", "runtime_manifest.py", "resource_isolation.py")})
(output / "difference-reference.json").write_bytes((json.dumps(metadata, ensure_ascii=False, indent=2) + "\n").encode("utf-8"))
print(difference["text"])
print(owned["text"])
