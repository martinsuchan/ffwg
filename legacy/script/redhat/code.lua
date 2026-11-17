
-- -----------------------------------------------------------------
-- Init
-- -----------------------------------------------------------------
local function prog_init()
    initModels()
    sound_playMusic("music/rybky02.ogg")
    local pokus = getRestartCount()


    -- -------------------------------------------------------------
    local function prog_init_room()
        local pom1, pom2, pomb1, pomb2 = 0, 0, false, false

        if pokus == 1 or random(100) < 50 then
            room.uvod = 1
            room.druha = random(2) + 2
        else
            room.uvod = 2
            room.druha = random(2) * 2 + 1
        end
        room.klavesnice = 0
        room.timerdruhe = random(1000) + 500

        return function()
            if no_dialog() and isReady(small) and isReady(big) then
                if room.timerdruhe > 0 then
                    room.timerdruhe = room.timerdruhe - 1
                end
                pom1 = 0
                if room.uvod > 0 then
                    pom1 = room.uvod
                    room.uvod = 0
                    if pokus ~= 1 then
                        adddel(random(500) + 20)
                    else
                        adddel(random(20) + 20)
                    end
                elseif room.timerdruhe == 0 then
                    pom1 = room.druha
                    room.timerdruhe = -1
                    adddel(30)
                elseif room.klavesnice == 0 and small.Y <= 3 then
                    room.klavesnice = 1
                    addm(10 + random(30), "redhat-m-keyboard")
                end
                if pom1 > 0 then
                    switch(pom1){
                        [1] = function()
                            addm(0, "redhat-m-where")
                            addv(0, "redhat-v-red")
                            addm(5, "redhat-m-what")
                            addv(5, "redhat-v-lie")
                            addm(10, "redhat-m-stop")
                        end,
                        [2] = function()
                            addv(0, "redhat-v-know")
                            addm(random(5), "redhat-m-dont")
                            addv(random(10) + 5, "redhat-v-denis")
                        end,
                        [3] = function()
                            addm(0, "redhat-m-machine")
                            addv(0, "redhat-v-suspicious")
                            addm(5, "redhat-m-choco")
                        end,
                    }
                end
            end
        end
    end

    -- --------------------
    local update_table = {}
    local subinit
    subinit = prog_init_room()
    if subinit then
        table.insert(update_table, subinit)
    end
    return update_table
end
local update_table = prog_init()


-- -----------------------------------------------------------------
-- Update
-- -----------------------------------------------------------------
function prog_update()
    for key, subupdate in pairs(update_table) do
        subupdate()
    end
end

