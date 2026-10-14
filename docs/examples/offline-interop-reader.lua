-- HD2Mimnt 离线读取示例；调用方传入自己的 JSON 解码器，不执行 JSON 内容。
local M = {}

local function default_read(path)
    local file, why = io.open(path, 'rb')
    if not file then return nil, why end
    local text = file:read('*a')
    file:close()
    return text
end

function M.read(root, decode, read_file)
    assert(type(root) == 'string' and type(decode) == 'function', 'root and JSON decoder required')
    read_file = read_file or default_read
    local function read_json(path)
        local ok, text = pcall(read_file, path)
        if not ok or type(text) ~= 'string' then return nil, 'file unavailable' end
        local parsed, value = pcall(decode, text)
        if not parsed or type(value) ~= 'table' then return nil, 'invalid JSON' end
        return value
    end

    -- 此示例仅支持 v1。未来适配器可加入 supported；不能盲读 latestMajorVersion。
    local supported = {[1] = true}
    local fallback = root .. '/interop/v1/runtime.json'
    local selected, major = fallback, 1
    local index = read_json(root .. '/interop/index.json')
    if index and index.format == 'hd2mimnt-interop-index' and index.schemaVersion == 1
        and type(index.versions) == 'table' then
        for _, version in ipairs(index.versions) do
            if supported[version.majorVersion] and version.majorVersion >= major
                and type(version.runtimePath) == 'string' then
                selected, major = version.runtimePath, version.majorVersion
            end
        end
    end

    local function valid(value)
        return value and value.format == 'hd2mimnt-runtime' and value.schemaVersion == 1
            and type(value.library) == 'table' and type(value.profiles) == 'table'
            and type(value.deployments) == 'table'
    end
    local snapshot, why = read_json(selected)
    if not valid(snapshot) then snapshot, why = read_json(fallback) end
    if not valid(snapshot) then return nil, why or 'unsupported runtime format' end
    return snapshot
end

-- 正式版 root = os.getenv('LOCALAPPDATA') .. '/HD2Mimnt'
-- local snapshot, why = M.read(root, json.decode)
-- snapshot.library 含未启用/未部署模组；snapshot.deployments 是实际部署追踪。
return M
