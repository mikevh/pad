export interface Match { path: string; score: number; indices: number[] }

const BOUNDARY = new Set(['/', '.', '_', '-', ' '])

/** Subsequence match with a bonus for consecutive characters, word starts, and matches inside the file name. */
export function fuzzyMatch(query: string, path: string): Match | null {
  const q = query.toLowerCase().replace(/\s+/g, '')
  if (!q) return { path, score: 0, indices: [] }
  const p = path.toLowerCase()
  const nameStart = p.lastIndexOf('/') + 1

  // Prefer matching entirely inside the file name when possible.
  const sub = p.indexOf(q, nameStart)
  if (sub >= 0) {
    const indices = Array.from({ length: q.length }, (_, i) => sub + i)
    return { path, score: 1000 + (sub === nameStart ? 200 : 0) - (p.length - q.length), indices }
  }

  const indices: number[] = []
  let from = 0
  for (const ch of q) {
    const i = p.indexOf(ch, from)
    if (i < 0) return null
    indices.push(i)
    from = i + 1
  }
  let score = 0
  indices.forEach((idx, n) => {
    score += 10
    if (n > 0 && idx === indices[n - 1] + 1) score += 15
    if (idx === 0 || BOUNDARY.has(p[idx - 1])) score += 20
    if (idx >= nameStart) score += 5
  })
  return { path, score: score - p.length / 10, indices }
}

export function rank(query: string, paths: string[], recent: string[], limit = 60): Match[] {
  if (!query.trim()) {
    const seen = new Set<string>()
    const out: Match[] = []
    for (const r of recent) if (paths.includes(r) && !seen.has(r)) { seen.add(r); out.push({ path: r, score: 0, indices: [] }) }
    for (const p of paths) if (!seen.has(p)) out.push({ path: p, score: 0, indices: [] })
    return out.slice(0, limit)
  }
  const out: Match[] = []
  for (const p of paths) {
    const m = fuzzyMatch(query, p)
    if (m) out.push(m)
  }
  return out.sort((a, b) => b.score - a.score || a.path.localeCompare(b.path)).slice(0, limit)
}
