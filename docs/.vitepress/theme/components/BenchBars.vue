<script setup lang="ts">
// Adds a proportional bar behind every number in table columns that hold
// measurements ("13.9 us", "1.60 MB", "1,083 us"), so a benchmark table reads at
// a glance. Lower is better everywhere these columns appear. Renders nothing.
import { useRoute } from 'vitepress'
import { nextTick, onMounted, watch } from 'vue'

const MEASURE = /^([\d,]+(?:\.\d+)?)\s*(ns|us|µs|ms|B|KB|MB)$/
const EMPTY = /^(—|-|)$/

function decorate() {
  document.querySelectorAll<HTMLTableElement>('.vp-doc table').forEach((table) => {
    if (table.dataset.benchBars) return
    table.dataset.benchBars = '1'
    const rows = [...table.tBodies[0]?.rows ?? []]
    if (rows.length < 2) return
    const columns = rows[0].cells.length
    for (let c = 0; c < columns; c++) {
      const cells = rows.map((r) => r.cells[c]).filter(Boolean)
      const parsed = cells.map((cell) => {
        const text = cell.textContent!.trim()
        if (EMPTY.test(text)) return { cell, value: null as number | null, unit: '' }
        const m = text.match(MEASURE)
        return m ? { cell, value: parseFloat(m[1].replace(/,/g, '')), unit: m[2] } : undefined
      })
      if (parsed.some((p) => !p)) continue
      const values = parsed.filter((p) => p!.value !== null)
      if (values.length < 2 || new Set(values.map((p) => p!.unit)).size !== 1) continue
      const max = Math.max(...values.map((p) => p!.value!))
      if (!(max > 0)) continue
      table.classList.add('bench-table')
      for (const p of values) {
        const pct = (p!.value! / max) * 100
        p!.cell.classList.add('bench-cell')
        p!.cell.style.setProperty('--bench', `${pct.toFixed(1)}%`)
        const row = p!.cell.parentElement as HTMLTableRowElement
        if (/KenseiECS/.test(row.cells[0]?.textContent ?? '') || c > 0 && /KenseiECS/.test(table.tHead?.rows[0]?.cells[c]?.textContent ?? '')) {
          p!.cell.classList.add('bench-cell--ours')
        }
      }
    }
  })
}

const route = useRoute()
onMounted(() => nextTick(decorate))
watch(() => route.path, () => nextTick(() => setTimeout(decorate)))
</script>

<template><span hidden /></template>
