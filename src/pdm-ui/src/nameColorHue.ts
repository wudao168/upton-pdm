export function nameColorHue(name: string) {
  let hash = 2166136261
  for (const letter of name) hash = Math.imul(hash ^ letter.codePointAt(0)!, 16777619)
  return (hash >>> 0) % 360
}
