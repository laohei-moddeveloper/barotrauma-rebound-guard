-- ReboundGuard native bridge v0.4.0; fallback retains the original Lua rule.
local function reboundRestoreMudraptorIsHuman()
    Hook.Patch("Barotrauma.Character", "get_IsHuman", {}, function(instance, ptable)
        return ptable.OriginalReturnValue or instance.Params.UseHumanAI
    end, Hook.HookMethodType.After)
end
local reboundOk, reboundInstalled = pcall(function()
    LuaUserData.RegisterType("ReboundGuard.NativeModHotPaths")
    local native = LuaUserData.CreateStatic("ReboundGuard.NativeModHotPaths")
    return native.InstallMudraptorIsHumanLua(reboundRestoreMudraptorIsHuman)
end)
if not reboundOk then print("[ReboundGuard] Mudraptor native bridge unavailable: " .. tostring(reboundInstalled)) end
if not reboundOk or not reboundInstalled then reboundRestoreMudraptorIsHuman() end
