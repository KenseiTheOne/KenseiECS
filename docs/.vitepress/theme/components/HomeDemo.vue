<script setup lang="ts">
import { withBase } from 'vitepress'
</script>

<template>
  <div class="home-demo">
    <a class="home-demo__shot" :href="withBase('/guide/demo')">
      <img
        :src="withBase('/horde-full.jpg')"
        alt="Horde at three minutes: thousands of enemies and the ECS inspector with per-system timings"
        width="1501"
        height="812"
        loading="lazy"
      />
    </a>
    <div class="home-demo__text">
      <p>
        <strong>Horde</strong> is a survivors-like game built on KenseiECS that runs in your browser on .NET WebAssembly.
        The horde grows to thousands of enemies; the stress slider pushes the world past 20,000 live entities.
      </p>
      <ul>
        <li>22 systems, each showing one feature of the library</li>
        <li>An ECS inspector with every system's time per frame, filter and pool sizes</li>
        <li>Toggle systems at runtime and watch the game react</li>
      </ul>
      <div class="home-demo__actions">
        <a class="home-demo__btn home-demo__btn--brand" :href="withBase('/guide/demo')">Play Horde</a>
        <a class="home-demo__btn" :href="withBase('/guide/inside-the-demo')">See how it's built</a>
      </div>
    </div>
  </div>
</template>

<style scoped>
.home-demo {
  display: grid;
  gap: 24px;
  margin-top: 16px;
}

@media (min-width: 960px) {
  .home-demo {
    grid-template-columns: 1.35fr 1fr;
    align-items: center;
    gap: 36px;
  }
}

.home-demo__shot {
  display: block;
  border-radius: 12px;
  overflow: hidden;
  border: 1px solid var(--vp-c-divider);
  box-shadow: 0 20px 48px -24px var(--kecs-glow);
  transition: transform 0.25s ease;
}

.home-demo__shot:hover { transform: translateY(-2px); }

.home-demo__shot img {
  display: block;
  width: 100%;
  height: auto;
}

.home-demo__text p { margin: 0 0 12px; }
.home-demo__text ul { margin: 0 0 20px; }

.home-demo__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
}

.vp-doc a.home-demo__btn {
  display: inline-block;
  padding: 0 20px;
  border-radius: 20px;
  line-height: 38px;
  font-size: 14px;
  font-weight: 600;
  text-decoration: none;
  border: 1px solid var(--vp-button-alt-border);
  background: var(--vp-button-alt-bg);
  color: var(--vp-button-alt-text);
  transition: background 0.2s, color 0.2s;
}

.vp-doc a.home-demo__btn:hover {
  background: var(--vp-button-alt-hover-bg);
  color: var(--vp-button-alt-hover-text);
}

.vp-doc a.home-demo__btn--brand {
  border-color: var(--vp-button-brand-border);
  background: var(--vp-button-brand-bg);
  color: var(--vp-button-brand-text);
}

.vp-doc a.home-demo__btn--brand:hover {
  background: var(--vp-button-brand-hover-bg);
  color: var(--vp-button-brand-hover-text);
}
</style>
