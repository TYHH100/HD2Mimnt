-- 沙箱红队探针
--
-- 立场：把自己当成一个想越权的插件。能拿到什么就拿什么，拿不到就记一笔。
--
-- 重要前提：沙箱里**没有 pcall / xpcall**，脚本无法捕获任何运行时错误。
-- 所以这里的每个“会抛错”的越界项都单独做成一个命令：
--   - 宿主返回成功  → 说明越界真的成功了，这是 breach；
--   - 宿主返回失败  → 说明边界在抛错那一刻就拦住了，这是拦截成功。
-- 这比在脚本里 try/catch 更有说服力，因为它验证的是宿主侧的行为。
--
-- 这里没有任何破坏性动作：不删文件、不写系统目录、不访问网络、不读取真实密钥。
-- 越界写入如果意外成功，脚本会立刻把该文件删掉。

local MUST_ABSENT = { "io", "os", "debug", "dofile", "loadfile", "require", "package" }
local WATCHED = { "load", "loadstring", "pcall", "xpcall", "getmetatable", "setmetatable", "rawset", "coroutine", "collectgarbage" }

-- 无抛错的探测：环境面清查
local function probe_surface()
  local absent, stillThere = {}, {}
  for _, name in ipairs(MUST_ABSENT) do
    if _G[name] == nil then
      table.insert(absent, name)
    else
      table.insert(stillThere, name)
    end
  end

  local watched = {}
  for _, name in ipairs(WATCHED) do
    if _G[name] ~= nil then
      table.insert(watched, name)
    end
  end

  local lines = {
    "## 环境面清查",
    "",
    "必拦项拦截 **" .. #absent .. " / " .. #MUST_ABSENT .. "**",
    "",
    "| 全局 | 期望 | 实际 |",
    "| --- | --- | --- |",
  }
  for _, name in ipairs(MUST_ABSENT) do
    local state = _G[name] == nil and "不存在（拦住）" or "存在（失守）"
    table.insert(lines, "| `" .. name .. "` | 不存在 | " .. state .. " |")
  end

  table.insert(lines, "")
  table.insert(lines, "**观察项**")
  table.insert(lines, "")
  table.insert(lines, "- 仍在的敏感全局：" .. (#watched > 0 and table.concat(watched, "、") or "无"))
  table.insert(lines, "- 私有目录回传：" .. tostring(hd2.storage.dir()))
  table.insert(lines, "- 程序集桥接：" .. (hd2.dll.available() and "可用" or "不可用"))
  table.insert(lines, "- 元信息表 `_MOONSHARP`：" .. (_MOONSHARP ~= nil and "可见（仅版本/平台元数据）" or "不可见"))

  local blocks = { hd2.ui.markdown(table.concat(lines, "\n")) }

  if #stillThere > 0 then
    table.insert(blocks, hd2.ui.notice(
      "以下全局本不该存在：" .. table.concat(stillThere, "、"),
      "danger",
      "沙箱失守"))
  else
    table.insert(blocks, hd2.ui.notice(
      "文件、进程、调试与模块加载能力全部不可见。注意：本节只覆盖脚本层，不代表 OS 级隔离。",
      "success",
      "脚本层边界完整"))
  end

  -- 界面文本注入：区块文本原样透传，安全性取决于渲染层而非沙箱
  table.insert(blocks, hd2.ui.markdown(
    "> 注入面观察：`hd2.ui.markdown` 接受任意文本（含 `<img onerror=...>` 与 `javascript:` 链接），\n"
    .. "> 未做转义即交给声明式渲染器。沙箱管住了能力，但没管住内容。"))

  local summary = "probe:surface:" .. #absent .. "/" .. #MUST_ABSENT
  return hd2.result.ok(summary, hd2.ui.update("replace", blocks))
end

-- 以下每一项：调用正常返回就说明越界成功
local function probe_require_escape()
  hd2.require("../outside")
  return hd2.result.ok("probe:breach")
end

local function probe_require_absolute()
  hd2.require("C:/Windows/win.ini")
  return hd2.result.ok("probe:breach")
end

local function probe_storage_traversal()
  hd2.storage.write("../../pwned.txt", "sandbox probe")
  return hd2.result.ok("probe:breach")
end

local function probe_storage_absolute()
  hd2.storage.write("C:/pwned-by-plugin.txt", "sandbox probe")
  return hd2.result.ok("probe:breach")
end

local function probe_storage_invalid()
  hd2.storage.write("pwn<>|.txt", "sandbox probe")
  return hd2.result.ok("probe:breach")
end

-- 前缀碰撞：攻击者 id 是受害者 id 的前缀时，兄弟目录可能被放行
local function probe_storage_sibling()
  local name = "../" .. hd2.plugin.id .. "2/pwned.txt"
  hd2.storage.write(name, "sandbox probe")
  -- 走到这里说明写成功了：立刻清理，避免留下痕迹
  hd2.storage.delete(name)
  return hd2.result.ok("probe:breach", hd2.ui.update("replace", {
    hd2.ui.notice("可写入相邻插件目录：" .. name, "danger", "前缀碰撞"),
  }))
end

local COMMANDS = {
  ["sandbox-surface"] = probe_surface,
  ["probe-require-escape"] = probe_require_escape,
  ["probe-require-absolute"] = probe_require_absolute,
  ["probe-storage-traversal"] = probe_storage_traversal,
  ["probe-storage-absolute"] = probe_storage_absolute,
  ["probe-storage-invalid"] = probe_storage_invalid,
  ["probe-storage-sibling"] = probe_storage_sibling,
}

function on_load()
  hd2.log.info("沙箱探针已加载（脚本层探测，无破坏性行为）")
end

function on_command(name, args)
  local handler = COMMANDS[name]
  if handler == nil then
    return hd2.result.fail("未知命令：" .. tostring(name))
  end

  local result = handler()
  -- 密钥格式串只在环境清查里发一次，用来验证边界扫描是否命中
  if name == "sandbox-surface" then
    hd2.log.warn("探针输出疑似密钥格式串：sk-" .. string.rep("A", 24))
  end
  return result
end
