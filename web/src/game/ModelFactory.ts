import { V2 } from "./V2";
import { Dir } from "./Dir";
import { Shape } from "./Shape";
import { Cube, Weight } from "./Cube";
import { ControlSym } from "./Unit";

/**
 * kind -> (weight, power, alive). Port of legacy/src/level/ModelFactory.cpp's
 * createParams(): fish_small/fish_big/item_light/item_heavy/item_fixed, plus
 * the "fish_extra"/"fish_EXTRA" couple used only by the windoze level
 * (docs/035). output_* border items are handled separately in createModel().
 */
function createParams(kind: string): { weight: Weight; power: Weight; alive: boolean } {
  switch (kind) {
    case "fish_small":
      return { weight: Weight.LIGHT, power: Weight.LIGHT, alive: true };
    case "fish_big":
      return { weight: Weight.LIGHT, power: Weight.HEAVY, alive: true };
    case "item_light":
      return { weight: Weight.LIGHT, power: Weight.NONE, alive: false };
    case "item_heavy":
      return { weight: Weight.HEAVY, power: Weight.NONE, alive: false };
    case "item_fixed":
      return { weight: Weight.FIXED, power: Weight.NONE, alive: false };
    default:
      // windoze's "old couple": fish_extra-wxyz (small, LIGHT power) and
      // fish_EXTRA-WXYZ (big, HEAVY power) - legacy ModelFactory::createParams.
      if (kind.startsWith("fish_extra")) {
        return { weight: Weight.LIGHT, power: Weight.LIGHT, alive: true };
      }
      if (kind.startsWith("fish_EXTRA")) {
        return { weight: Weight.LIGHT, power: Weight.HEAVY, alive: true };
      }
      throw new Error(`unknown/unimplemented model kind: ${kind}`);
  }
}

const OUTPUT_DIRS: Record<string, Dir | undefined> = {
  output_left: Dir.LEFT,
  output_right: Dir.RIGHT,
  output_up: Dir.UP,
  output_down: Dir.DOWN,
};

/** legacy ModelFactory::createOutputItem: a FIXED cube with a one-way exit
 *  direction (output_left/right/up/down). windoze's spuntik only. */
function createOutputItem(kind: string, loc: V2, shape: string): Cube {
  const dir = OUTPUT_DIRS[kind];
  if (dir === undefined) throw new Error(`unknown output dir: ${kind}`);
  const cube = new Cube(kind, loc, Weight.FIXED, Weight.NONE, false, new Shape(shape));
  cube.setOutDir(dir);
  return cube;
}

export function createModel(kind: string, loc: V2, shape: string): Cube {
  if (kind.startsWith("output_")) {
    return createOutputItem(kind, loc, shape);
  }
  const { weight, power, alive } = createParams(kind);
  return new Cube(kind, loc, weight, power, alive, new Shape(shape));
}

/** Any driveable fish, including windoze's extra couple - used for both unit
 *  creation and rendering (extra fish animate through the normal fish path). */
export function isFishKind(kind: string): boolean {
  return (
    kind === "fish_small" ||
    kind === "fish_big" ||
    kind.startsWith("fish_extra") ||
    kind.startsWith("fish_EXTRA")
  );
}

/** legacy ModelFactory::parseExtraControlSym: the four move symbols are encoded
 *  in the kind string itself ("fish_extra-UDLR" -> up,down,left,right at the 4
 *  chars after the 11-char "fish_extra-" prefix). Case in the prefix is ignored
 *  (fish_EXTRA-WXYZ works too) - only length + trailing chars matter, exactly
 *  as the original. */
const EXTRA_PREFIX_LEN = "fish_extra-".length;
export function parseExtraControlSym(kind: string): ControlSym {
  if (kind.length !== EXTRA_PREFIX_LEN + 4) {
    throw new Error(`extra fish kind must specify 4 control symbols: ${kind}`);
  }
  return {
    up: kind[EXTRA_PREFIX_LEN],
    down: kind[EXTRA_PREFIX_LEN + 1],
    left: kind[EXTRA_PREFIX_LEN + 2],
    right: kind[EXTRA_PREFIX_LEN + 3],
  };
}
