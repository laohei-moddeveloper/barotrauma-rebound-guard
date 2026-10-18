-- ReboundGuard native bridge v0.4.0; no affliction or movement rule changes.
local function HasAffliction(character, identifier)
    if character == nil or character.CharacterHealth == nil then return false end
    local aff = character.CharacterHealth.GetAffliction(identifier)
    return aff ~= nil and aff.Strength >= 1
end
local function HasExistingBuff(character)
    return HasAffliction(character, "tsm_straight_grip_buff")
        or HasAffliction(character, "tsm_buff_dsdj_exo_bag")
        or HasAffliction(character, "tsm_analgesic_buff")
end
local function reboundRestoreTSMBuffs()
    Hook.Patch("tsm_remove_aim_wobble", "Barotrauma.AnimController", "GetAimWobble", function(instance, ptable)
        if HasExistingBuff(instance.Character) then ptable.PreventExecution = true; return 0 end
    end, Hook.HookMethodType.Before)
    Hook.Patch("tsm_remove_movement_penalty", "Barotrauma.Character", "CalculateMovementPenalty", function(instance, ptable)
        if HasExistingBuff(instance) then ptable.PreventExecution = true; return 0 end
    end, Hook.HookMethodType.Before)
end
local reboundOk, reboundInstalled = pcall(function()
    LuaUserData.RegisterType("ReboundGuard.NativeModHotPaths")
    local native = LuaUserData.CreateStatic("ReboundGuard.NativeModHotPaths")
    return native.InstallTSMBuffHooksLua(reboundRestoreTSMBuffs)
end)
if not reboundOk then print("[ReboundGuard] TSM native bridge unavailable: " .. tostring(reboundInstalled)) end
if not reboundOk or not reboundInstalled then reboundRestoreTSMBuffs() end
