/** Integer grid vector. Port of legacy/src/gengine/V2.h. */
export class V2 {
  constructor(
    public readonly x: number,
    public readonly y: number,
  ) {}

  plus(other: V2): V2 {
    return new V2(this.x + other.x, this.y + other.y);
  }

  minus(other: V2): V2 {
    return new V2(this.x - other.x, this.y - other.y);
  }

  scale(rate: number): V2 {
    return new V2(this.x * rate, this.y * rate);
  }

  equals(other: V2): boolean {
    return this.x === other.x && this.y === other.y;
  }

  toString(): string {
    return `[${this.x},${this.y}]`;
  }
}
