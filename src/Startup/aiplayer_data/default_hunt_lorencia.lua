if is_dead() then wait(3) end
if not_buffed() then walk_to_npc(257) end
if not_buffed() then talk_npc(257) end
if not_buffed() then get_buff() end
if hp_below(40) then use_hp_potion() end
if has_target() then attack_target() end
pickup_nearby()
random_walk(10)
