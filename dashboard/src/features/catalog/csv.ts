import type { CatalogItemInput } from '../../api/types'

export function parseCsv(text: string): string[][] {
  const rows: string[][] = []
  let row: string[] = []
  let field = ''
  let quoted = false
  for (let i = 0; i < text.length; i++) {
    const ch = text[i]
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { field += '"'; i++ }
      else if (ch === '"') quoted = false
      else field += ch
    } else if (ch === '"') quoted = true
    else if (ch === ',' || ch === ';') { row.push(field); field = '' }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++
      row.push(field); field = ''
      if (row.some((f) => f.trim() !== '')) rows.push(row)
      row = []
    } else field += ch
  }
  row.push(field)
  if (row.some((f) => f.trim() !== '')) rows.push(row)
  return rows
}

const COLUMNS = ['item_id', 'title', 'category', 'price', 'currency', 'in_stock', 'url', 'image_url'] as const

export function itemsFromCsv(text: string): { items: CatalogItemInput[]; errors: string[] } {
  const [header, ...rows] = parseCsv(text)
  const errors: string[] = []
  if (!header) return { items: [], errors: ['The file is empty'] }
  const index = Object.fromEntries(header.map((h, i) => [h.trim().toLowerCase(), i]))
  if (index.item_id === undefined || index.title === undefined) return { items: [], errors: ['The header needs at least item_id and title columns'] }

  const items: CatalogItemInput[] = []
  rows.forEach((r, n) => {
    const get = (c: (typeof COLUMNS)[number]) => (index[c] === undefined ? undefined : r[index[c]]?.trim())
    const id = get('item_id')
    const title = get('title')
    if (!id || !title) { errors.push(`Row ${n + 2}: item_id and title are required`); return }
    const price = get('price')
    const stock = get('in_stock')?.toLowerCase()
    items.push({
      item_id: id,
      title,
      category: get('category') ?? '',
      price: price ? Number(price.replace(',', '.')) : null,
      currency: get('currency') || undefined,
      in_stock: stock === undefined || stock === '' ? true : !['false', '0', 'no', 'n'].includes(stock),
      url: get('url') ?? '',
      image_url: get('image_url') ?? '',
    })
    if (price && Number.isNaN(items[items.length - 1].price)) errors.push(`Row ${n + 2}: price "${price}" is not a number`)
  })
  return { items, errors }
}
