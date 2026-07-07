import { Dir } from "./Dir";
import { Cube, Weight } from "./Cube";

/** A recursive "is this model, directly or through a stack, resting on X" test. */
export interface OnCondition {
  isSatisfy(model: Cube): boolean;
  isWrong(model: Cube): boolean;
}

/**
 * Object rests on another inert object that is itself safely (strong-pad)
 * supported and not currently moving - i.e. it can be pushed/carried over
 * a fish's back without crushing it. Port of legacy/src/level/OnStack.h.
 */
export const OnStack: OnCondition = {
  isSatisfy(model) {
    return (
      !model.isAlive &&
      model.rules.dir === Dir.NO &&
      model.rules.isOnStrongPad(Weight.LIGHT)
    );
  },
  isWrong(model) {
    return model.isAlive;
  },
};

/** Port of legacy/src/level/OnWall.h. */
export const OnWall: OnCondition = {
  isSatisfy(model) {
    return model.isWall;
  },
  isWrong(model) {
    return model.isAlive;
  },
};

/** Port of legacy/src/level/OnStrongPad.h. */
export function onStrongPad(weight: Weight): OnCondition {
  return {
    isSatisfy(model) {
      return model.isWall || (model.isAlive && model.power >= weight);
    },
    isWrong(model) {
      return model.isAlive && model.power < weight;
    },
  };
}
