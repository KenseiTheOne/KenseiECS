<script setup lang="ts">
import { withBase } from 'vitepress'

// Numbers from BENCHMARKS.md (BenchmarkDotNet, .NET 8, 10,000 entities) and the demo.
const stats = [
  { value: '5.5 µs', label: 'to iterate 10,000 entities with an owning group', link: '/benchmarks#iteration-—-component-traversal' },
  { value: '0 B', label: 'allocated by iteration and structural changes', link: '/benchmarks' },
  { value: '2.3×', label: 'faster game-loop frame than Arch, 1.2× than LeoEcsLite', link: '/benchmarks#game-loop-—-a-realistic-frame' },
  { value: '20,000+', label: 'live entities in the browser demo', link: '/guide/demo' },
]
</script>

<template>
  <div class="home-stats">
    <a v-for="s in stats" :key="s.value" class="home-stats__item" :href="withBase(s.link)">
      <span class="home-stats__value">{{ s.value }}</span>
      <span class="home-stats__label">{{ s.label }}</span>
    </a>
  </div>
</template>

<style scoped>
.home-stats {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 1px;
  margin: 0 auto 40px;
  padding: 0 24px;
  max-width: 1152px;
}

@media (min-width: 640px) {
  .home-stats { padding: 0 48px; }
}

@media (min-width: 960px) {
  .home-stats { grid-template-columns: repeat(4, 1fr); padding: 0 64px; }
}

.home-stats__item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 18px 20px;
  border-left: 2px solid var(--vp-c-divider);
  text-decoration: none;
  transition: border-color 0.2s;
}

.home-stats__item:hover {
  border-left-color: var(--vp-c-brand-1);
}

.home-stats__value {
  font-size: 30px;
  font-weight: 700;
  line-height: 1.15;
  letter-spacing: -0.02em;
  color: var(--vp-c-text-1);
  font-variant-numeric: tabular-nums;
}

.home-stats__label {
  font-size: 13px;
  line-height: 1.45;
  color: var(--vp-c-text-2);
}
</style>
