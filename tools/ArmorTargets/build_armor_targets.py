#!/usr/bin/env python3
"""从 HD2SDK 的护甲数据生成 HD2Mimnt 的目标护甲目录。

输入（HD2SDK-AQ-Edition/hd2_system/）：
  resource_binding_targets.json   —— 权威绑定闭包：archive → domain + equipment(key, unit_ids)
  armor_table/armor_runtime_targets.json —— archive → 英文护甲名
  armor_table/armor_part_unit_ids.json   —— 护甲代号 → 中文名 + 部位槽位 Unit 归属

输出：src/HD2Mimnt.Application/Config/ArmorTargets.json

用法：
  python tools/ArmorTargets/build_armor_targets.py <HD2SDK-AQ-Edition 根目录> <输出 json>
"""
import json
import sys
from pathlib import Path


def load(path):
    with open(path, encoding="utf-8") as handle:
        return json.load(handle)


def main() -> int:
    if len(sys.argv) != 3:
        print(__doc__)
        return 2
    sdk = Path(sys.argv[1])
    system = sdk / "hd2_system"
    binding = load(system / "resource_binding_targets.json")
    runtime = load(system / "armor_table" / "armor_runtime_targets.json")
    parts = load(system / "armor_table" / "armor_part_unit_ids.json")

    # 部位槽位 → Unit 集合（十进制字符串），用于挑载体 Unit 与给头盔补名字。
    slot_units = {name: {int(value) for value in rows} for name, rows in parts["part_slots"].items()}
    head_owner = {}
    for code, item in parts["items"].items():
        for row in item.get("slots", {}).get("Head", []):
            head_owner.setdefault(int(row["unit_id"]), set()).add(code)

    named = {key: value.get("display_name", "") for key, value in runtime["targets"].items()}
    targets = []
    for archive, entry in sorted(binding["targets"].items()):
        equipment = entry.get("equipment") or []
        if not equipment:
            continue
        # 只有每一份 equipment 都拥有的 Unit 才能安全路由（与 SDK 的清理闭包一致）。
        shared = {int(value, 16) for value in equipment[0]["unit_ids"]}
        for row in equipment[1:]:
            shared &= {int(value, 16) for value in row["unit_ids"]}
        if not shared:
            continue
        domain = entry["domain"]
        units = sorted(shared)

        # 护甲代号与显示名：身体用运行时目标库的英文名，头盔用中文部位表按 Head Unit 反查。
        code, name = "", named.get(archive, "").strip()
        if name:
            code = name.split(" ")[0]
        else:
            codes = set()
            for unit in units:
                codes |= head_owner.get(unit, set())
            if codes:
                code = sorted(codes)[0]
                source_names = parts["items"][code].get("source_names") or []
                name = source_names[0] if source_names else ""

        preferred = "Head" if domain == "HELMET" else "Torso"
        preferred_units = slot_units.get(preferred, set())
        carrier = next((unit for unit in units if unit in preferred_units), units[0])

        targets.append({
            "archive": archive,
            "domain": domain,
            "code": code,
            "name": name,
            "carrier": f"{carrier:016x}",
            "equipment": [row["key"] for row in equipment],
            "units": [f"{unit:016x}" for unit in units],
        })

    document = {
        "schema": "HD2MimntArmorTargets1",
        "source": {
            "binding": binding.get("source", ""),
            "runtime_targets": "armor_runtime_targets.json",
            "part_catalog": "armor_part_unit_ids.json",
            "note": "units 是每份 equipment 都拥有的 Unit 交集；carrier 是接管整身的载体 Unit，其余 Unit 由转换器写成零面积占位。",
        },
        "targets": targets,
    }
    output = Path(sys.argv[2])
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(document, ensure_ascii=False, indent=1) + "\n", encoding="utf-8")

    body = sum(1 for row in targets if row["domain"] == "BODY")
    print(f"{len(targets)} targets ({body} BODY, {len(targets) - body} HELMET)")
    print(f"named: {sum(1 for row in targets if row['name'])}")
    print(f"{output} = {output.stat().st_size:,} bytes")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
