import DefaultTheme from 'vitepress/theme'
import type { Theme } from 'vitepress'
import { h } from 'vue'
import HeroShot from './components/HeroShot.vue'
import HomeStats from './components/HomeStats.vue'
import HomeDemo from './components/HomeDemo.vue'
import BenchBars from './components/BenchBars.vue'
import './custom.css'

export default {
  extends: DefaultTheme,
  Layout() {
    return h(DefaultTheme.Layout, null, {
      'home-hero-image': () => h(HeroShot),
      'home-features-before': () => h(HomeStats),
      'layout-bottom': () => h(BenchBars),
    })
  },
  enhanceApp({ app }) {
    app.component('HomeDemo', HomeDemo)
  },
} satisfies Theme
