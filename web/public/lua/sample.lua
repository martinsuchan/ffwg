-- Standalone POC script, fetched at runtime (not bundled) and executed by wasmoon.
-- Exercises three directions of the Lua <-> JS boundary that the real port will need:
--   1. one-way call out to a host function (host_log)
--   2. call out with a return value (host_add)
--   3. passing/returning a table (host_describe_fish)

host_log("hello from sample.lua")

local sum = host_add(2, 3)
host_log("host_add(2, 3) = " .. sum)

for i = 1, 3 do
    host_log("loop iteration " .. i)
end

local fish = { name = "small", x = 10, y = 4 }
local moved = host_describe_fish(fish)
host_log("host_describe_fish -> " .. moved.name .. " at (" .. moved.x .. "," .. moved.y .. ")")

host_log("sample.lua finished")
