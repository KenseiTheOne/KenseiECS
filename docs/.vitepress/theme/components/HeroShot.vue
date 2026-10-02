<script setup lang="ts">
import { withBase } from 'vitepress'
</script>

<template>
  <a class="hero-shot" :href="withBase('/guide/demo')" aria-label="Play Horde, the live demo">
    <img
      :src="withBase('/horde-hero.jpg')"
      alt="Horde: thousands of enemies chasing the player, simulated by KenseiECS in the browser"
      width="820"
      height="640"
    />
    <span class="hero-shot__badge"><span class="hero-shot__dot" />Live demo · .NET WebAssembly</span>
    <span class="hero-shot__play">
      <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M8 5.5v13l11-6.5z" fill="currentColor" /></svg>
      Play in your browser
    </span>
  </a>
</template>

<style scoped>
.hero-shot {
  position: relative;
  display: block;
  width: 100%;
  max-width: 560px;
  aspect-ratio: 820 / 640;
  border-radius: 16px;
  overflow: hidden;
  border: 1px solid var(--vp-c-divider);
  box-shadow: 0 24px 64px -24px var(--kecs-glow), 0 0 0 1px rgba(255, 255, 255, 0.04) inset;
  background: #0b0d12;
  transition: transform 0.25s ease, box-shadow 0.25s ease;
}

.hero-shot:hover {
  transform: translateY(-2px);
  box-shadow: 0 32px 80px -24px var(--kecs-glow), 0 0 0 1px rgba(255, 255, 255, 0.08) inset;
}

.hero-shot img {
  display: block;
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.hero-shot__badge {
  position: absolute;
  top: 14px;
  left: 14px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 5px 11px;
  border-radius: 999px;
  background: rgba(11, 13, 18, 0.78);
  backdrop-filter: blur(6px);
  color: rgba(255, 255, 255, 0.92);
  font-size: 12px;
  font-weight: 500;
}

.hero-shot__dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
  background: #4cc38a;
  box-shadow: 0 0 0 3px rgba(76, 195, 138, 0.25);
}

.hero-shot__play {
  position: absolute;
  left: 50%;
  bottom: 18px;
  transform: translateX(-50%);
  display: inline-flex;
  align-items: center;
  gap: 8px;
  padding: 10px 18px;
  border-radius: 999px;
  background: var(--vp-c-brand-3);
  color: #fff;
  font-size: 14px;
  font-weight: 600;
  white-space: nowrap;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.35);
  transition: background 0.2s;
}

.hero-shot:hover .hero-shot__play {
  background: var(--vp-c-brand-2);
}

.hero-shot__play svg {
  width: 16px;
  height: 16px;
}
</style>
