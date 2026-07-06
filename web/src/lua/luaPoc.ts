import { LuaFactory } from "wasmoon";

export interface LuaPocResult {
  logLines: string[];
}

interface FishTable {
  name: string;
  x: number;
  y: number;
}

/**
 * Fetches a standalone .lua file and runs it in a fresh wasmoon engine,
 * exercising host callbacks (one-way, with a return value, and with a table).
 */
export async function runLuaPoc(scriptUrl: string): Promise<LuaPocResult> {
  const logLines: string[] = [];

  const response = await fetch(scriptUrl);
  if (!response.ok) {
    throw new Error(`failed to fetch ${scriptUrl}: ${response.status}`);
  }
  const source = await response.text();

  const factory = new LuaFactory();
  const lua = await factory.createEngine();

  try {
    lua.global.set("host_log", (message: string) => {
      logLines.push(message);
      console.log("[lua]", message);
    });

    lua.global.set("host_add", (a: number, b: number) => a + b);

    lua.global.set("host_describe_fish", (fish: FishTable) => ({
      name: fish.name,
      x: fish.x + 1,
      y: fish.y + 1,
    }));

    await lua.doString(source);
  } finally {
    lua.global.close();
  }

  return { logLines };
}
