import { V2 } from "./V2";
import { Shape } from "./Shape";
import { Cube, Weight } from "./Cube";

/**
 * kind -> (weight, power, alive). Port of the relevant branches of
 * legacy/src/level/ModelFactory.cpp's createParams() - only the kinds
 * that actually appear in legacy/script/**\/models.lua across the corpus
 * this POC targets (fish_small/fish_big/item_light/item_heavy/item_fixed).
 * output_* border items (windoze-only) and fish_extra* are not implemented.
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
      throw new Error(`unknown/unimplemented model kind: ${kind}`);
  }
}

export function createModel(kind: string, loc: V2, shape: string): Cube {
  const { weight, power, alive } = createParams(kind);
  return new Cube(kind, loc, weight, power, alive, new Shape(shape));
}

export function isFishKind(kind: string): boolean {
  return kind === "fish_small" || kind === "fish_big";
}
