import { build } from 'esbuild'
import { readFileSync } from 'node:fs'

const { version } = JSON.parse(readFileSync('package.json', 'utf8'))
const common = {
  entryPoints: ['src/index.ts'],
  bundle: true,
  target: 'es2020',
  define: { __VERSION__: JSON.stringify(version) },
  logLevel: 'info',
}

await build({ ...common, format: 'esm', outfile: 'dist/reapdeep.esm.js' })
await build({ ...common, format: 'iife', globalName: 'reapdeep', minify: true, outfile: 'dist/reapdeep.min.js' })
