
createRoom(33, 31, "images/"..codename.."/redhat1-p.png")
setRoomWaves(5, 10, 5)

room = addModel("item_fixed", 0, 0,
[[
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
XX.............................XX
XX.............................XX
XXX...........................XXX
XXXXXXXXXXXXX.......XXXXXXXXXXXXX
XXXXXXXXXXXXX.......XXXXXXXXXXXXX
XXXXXXXXXXXXXX.....XXXX..XXXXXXXX
XXXXXXXXX..........XXX.....XXXXXX
XXXXXXXXX............XX.....XXXXX
XXXXXXXX.....................XXXX
XXXXXXXXX....................XXXX
XXXXXX.....X................XXXXX
XXXXX....................XXXXXXXX
XXXXX....................XXXXXXXX
XXXX......................XXXXXXX
XXXX.......................XXXXXX
XXX........................XXXXXX
XXX........................XXXXXX
XX.........................X...XX
XX..............................X
XX.........................XX...X
XX...........X...................
XX..........XX...................
XX.........XXXXXXXX......X.XXXXXX
XX..........XXXXXXX......XXXXXXXX
XXX..........XXXXX........XXXXXXX
XXX..........XXXXX........XXXXXXX
XXX..........XXXXXX.......XXXXXXX
XXX..........XXXXXX......XXXXXXXX
XXXX.........XXXXXXX.XX..XXXXXXXX
XXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXXX
]])
addItemAnim(room, "images/"..codename.."/redhat1-w.png")

item_light = addModel("item_light", 10, 9,
[[
XX.
.XX
]])
addItemAnim(item_light, "images/"..codename.."/4.png")

item_light = addModel("item_light", 19, 27,
[[
.X.
XXX
]])
addItemAnim(item_light, "images/"..codename.."/8.png")

item_light = addModel("item_light", 13, 4,
[[
X.
XX
.X
]])
addItemAnim(item_light, "images/"..codename.."/2.png")

item_light = addModel("item_light", 18, 4,
[[
.X
XX
X.
]])
addItemAnim(item_light, "images/"..codename.."/7.png")

item_light = addModel("item_light", 24, 10,
[[
XX
XX
]])
addItemAnim(item_light, "images/"..codename.."/5.png")

item_light = addModel("item_light", 22, 7,
[[
XX
.X
.X
]])
addItemAnim(item_light, "images/"..codename.."/3.png")

item_light = addModel("item_light", 7, 27,
[[
X.
XX
X.
]])
addItemAnim(item_light, "images/"..codename.."/9.png")

item_light = addModel("item_light", 6, 26,
[[
XXX
..X
]])
addItemAnim(item_light, "images/"..codename.."/1.png")

item_light = addModel("item_light", 12, 19,
[[
X..
XXX
]])
addItemAnim(item_light, "images/"..codename.."/6.png")

item_light = addModel("item_light", 16, 22,
[[
X
]])
addItemAnim(item_light, "images/"..codename.."/flop.png")

item_light = addModel("item_light", 21, 3,
[[
XXXXXX
]])
addItemAnim(item_light, "images/"..codename.."/keyboard.png")

item_light = addModel("item_light", 27, 21,
[[
X
X
]])
addItemAnim(item_light, "images/"..codename.."/flash.png")


small = addModel("fish_small", 7, 16,
[[
XXX
]])
addFishAnim(small, LOOK_RIGHT, "images/fishes/small")

big = addModel("fish_big", 7, 14,
[[
XXXX
XXXX
]])
addFishAnim(big, LOOK_RIGHT, "images/fishes/big")



