-- 纯 Lua 插件示例：没有程序集，也没有编译步骤。
--
-- 脚本能触达宿主的方式只有 hd2 表：
--   hd2.log / hd2.storage / hd2.config / hd2.ai / hd2.json / hd2.ui / hd2.result
-- 没有 io、os、debug、loadfile、require("...")、网络，也没有任意反射。
-- 需要底层能力时挂一个 DLL，用 hd2.dll.call 调用（见 LuaHybrid 示例）。

local COUNTER_KEY = "run-count"

-- 生命周期钩子：on_load / on_shutdown（均可选）
function on_load()
  hd2.log.info("Lua 模组体检已加载，数据目录：" .. hd2.storage.dir())
end

function on_shutdown()
  hd2.log.info("Lua 模组体检卸载")
end

local function bump_counter()
  local count = (tonumber(hd2.storage.read(COUNTER_KEY)) or 0) + 1
  hd2.storage.write(COUNTER_KEY, tostring(count))
  return count
end

local function is_blank(value)
  return value == nil or value == ""
end

-- 把模组信息整理成事实表；缺失字段单独收集，用于给出一条可执行的建议
local function inspect(mod)
  local fields = {
    { label = "版本", value = mod.version },
    { label = "作者", value = mod.author },
    { label = "说明", value = mod.description },
  }

  local facts, missing = {}, {}
  for _, field in ipairs(fields) do
    if is_blank(field.value) then
      table.insert(facts, { label = field.label, value = "（缺失）" })
      table.insert(missing, field.label)
    else
      table.insert(facts, { label = field.label, value = field.value })
    end
  end
  return facts, missing
end

-- 命令分发。args 是宿主传进来的参数表；
-- 从模组右键菜单触发时含 mod = { id, name, version, author, description } 与 text。
function on_command(name, args)
  if name ~= "lua-mod-check" then
    return hd2.result.fail("未知命令：" .. tostring(name))
  end

  local count = bump_counter()
  local mod = args.mod or {}
  local title = is_blank(mod.name) and "当前模组" or mod.name

  local facts, missing = inspect(mod)

  local blocks = {
    hd2.ui.markdown("## " .. title .. "\n\n> 本报告由 Lua 脚本生成，没有编译产物。"),
    hd2.ui.facts(facts),
  }

  if #missing > 0 then
    table.insert(blocks, hd2.ui.notice(
      "缺少：" .. table.concat(missing, "、") .. "。补全后模组信息会更完整。",
      "warning",
      "信息不完整"))
  else
    table.insert(blocks, hd2.ui.notice("信息齐备，可以直接部署。", "success"))
  end

  table.insert(blocks, hd2.ui.divider())
  table.insert(blocks, hd2.ui.markdown("累计执行 **" .. count .. "** 次（计数存在插件私有目录）"))

  table.insert(blocks, hd2.ui.actions({
    { label = "再执行一次", icon = "Play", style = "primary", command = "lua-mod-check" },
    { label = "脚本插件说明", icon = "Info", dialog = "lua-check-help" },
  }))

  return hd2.result.ok(nil, hd2.ui.update("replace", blocks))
end
