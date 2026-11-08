-- 混合插件示例：Lua 主控 + DLL 提供底层能力。
--
-- 分工：
--   * 脚本负责流程、条件判断与界面编排（改一行就生效，不用重编译）；
--   * 需要哈希、字节统计这类底层操作时，调用 hd2.dll.call 交给程序集。
--
-- DLL 里的命令以 PluginResult 返回，脚本拿到的是一张表：
--   { success = true, message = "…", ui = { mode = "replace", blocks = { … } } }

local DIGEST_COMMAND = "hybrid-digest"

function on_load()
  if hd2.dll.available() then
    hd2.log.info("混合插件已加载，可调用命令：" .. table.concat(hd2.dll.names(), ", "))
  else
    hd2.log.warn("混合插件已加载，但程序集未挂载；只能使用脚本自身能力")
  end
end

local function join_arguments(mod)
  local parts = {}
  if mod.name and mod.name ~= "" then table.insert(parts, mod.name) end
  if mod.description and mod.description ~= "" then table.insert(parts, mod.description) end
  if #parts == 0 then
    table.insert(parts, "空模组信息")
  end
  return table.concat(parts, "\n")
end

function on_command(name, args)
  if name ~= "hybrid-report" then
    return hd2.result.fail("未知命令：" .. tostring(name))
  end

  local mod = args.mod or {}
  local title = (mod.name and mod.name ~= "") and mod.name or "未选择模组"

  local blocks = {
    hd2.ui.markdown("## " .. title .. "\n\n脚本组织结构，指纹由 DLL 计算。"),
  }

  if hd2.dll.available() and hd2.dll.has(DIGEST_COMMAND) then
    -- 多返回值：成功时只有结果表，失败时第二个值是原因
    local result, err = hd2.dll.call(DIGEST_COMMAND, { text = join_arguments(mod) })

    if result == nil then
      table.insert(blocks, hd2.ui.notice("DLL 调用失败：" .. tostring(err), "danger"))
    else
      -- DLL 用声明式区块返回数据，脚本原样并入自己的报告
      if result.ui and result.ui.blocks then
        for _, block in ipairs(result.ui.blocks) do
          table.insert(blocks, block)
        end
      end
      if result.message then
        table.insert(blocks, hd2.ui.notice(result.message, "success", "底层计算完成"))
      end
    end
  else
    table.insert(blocks, hd2.ui.notice(
      "没有可用的程序集，" .. DIGEST_COMMAND .. " 无法执行；脚本部分仍然正常工作。",
      "warning"))
  end

  table.insert(blocks, hd2.ui.divider())
  table.insert(blocks, hd2.ui.actions({
    { label = "再算一次", icon = "Play", style = "primary", command = "hybrid-report" },
  }))

  return hd2.result.ok(nil, hd2.ui.update("replace", blocks))
end
