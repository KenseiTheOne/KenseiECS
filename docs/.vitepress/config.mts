import { defineConfig } from 'vitepress'

const repo = 'https://github.com/KenseiTheOne/KenseiECS'

export default defineConfig({
  title: 'KenseiECS',
  description: 'Lightweight sparse-set ECS for Unity and .NET',
  base: '/KenseiECS/',
  cleanUrls: true,
  lastUpdated: true,
  // The repo-level docs index is for GitHub browsing; the site has its own home page.
  srcExclude: ['README.md', 'snippets/**'],
  head: [['link', { rel: 'icon', href: '/KenseiECS/favicon.svg' }]],
  markdown: { theme: { light: 'github-light', dark: 'github-dark' } },
  themeConfig: {
    logo: '/favicon.svg',
    nav: [
      { text: 'Guide', link: '/guide/introduction', activeMatch: '/(guide|concepts|guides)/' },
      { text: 'Unity', link: '/unity/bootstrap', activeMatch: '/unity/' },
      { text: 'Reference', link: '/architecture', activeMatch: '/(architecture|benchmarks|faq|migration-from-leoecslite|changelog|contributing)' },
      { text: 'v2.0.0', items: [
        { text: 'Changelog', link: '/changelog' },
        { text: 'Releases', link: `${repo}/releases` },
      ] },
    ],
    sidebar: [
      { text: 'Getting Started', items: [
        { text: 'Introduction', link: '/guide/introduction' },
        { text: 'Installation', link: '/guide/installation' },
        { text: 'Quick Start', link: '/guide/quick-start' },
      ] },
      { text: 'Core Concepts', items: [
        { text: 'Entities', link: '/concepts/entities' },
        { text: 'Components', link: '/concepts/components' },
        { text: 'Filters', link: '/concepts/filters' },
        { text: 'Systems & SharedData', link: '/concepts/systems' },
        { text: 'SystemsRunner', link: '/concepts/runner' },
      ] },
      { text: 'Features', items: [
        { text: 'Groups', link: '/guides/groups' },
        { text: 'Change Tracking', link: '/guides/change-tracking' },
        { text: 'CommandBuffer', link: '/guides/command-buffer' },
        { text: 'Singletons', link: '/guides/singletons' },
        { text: 'OneFrame Components', link: '/guides/one-frame' },
        { text: 'Component Listeners', link: '/guides/component-listeners' },
        { text: 'World Events', link: '/guides/world-events' },
        { text: 'World Lifecycle', link: '/guides/world-lifecycle' },
        { text: 'Snapshots', link: '/guides/snapshots' },
        { text: 'Generated Init', link: '/guides/source-generator' },
        { text: 'Threading', link: '/guides/threading' },
        { text: 'WorldConfig', link: '/guides/world-config' },
        { text: 'Release vs. KENSEI_DEBUG', link: '/guides/debug-mode' },
      ] },
      { text: 'Unity', items: [
        { text: 'Bootstrap & Authoring', link: '/unity/bootstrap' },
        { text: 'EcsEntityView', link: '/unity/entity-view' },
        { text: 'Listener Bridge', link: '/unity/listener-bridge' },
        { text: 'Debug Tools', link: '/unity/debug-tools' },
        { text: 'BasicGame Sample', link: '/unity/sample' },
      ] },
      { text: 'Reference', items: [
        { text: 'Architecture', link: '/architecture' },
        { text: 'Benchmarks', link: '/benchmarks' },
        { text: 'Migrating from LeoEcsLite', link: '/migration-from-leoecslite' },
        { text: 'FAQ', link: '/faq' },
        { text: 'Changelog', link: '/changelog' },
        { text: 'Contributing', link: '/contributing' },
      ] },
    ],
    socialLinks: [{ icon: 'github', link: repo }],
    editLink: { pattern: `${repo}/edit/main/docs/:path` },
    search: { provider: 'local' },
    outline: { level: [2, 3] },
    footer: { message: 'Released under the MIT License.' },
  },
})
