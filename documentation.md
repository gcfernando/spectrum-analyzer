---
layout: default
title: "Spectrum Audio Analyzer — Documentation and Guides"
description: "A Windows WASAPI audio spectrum analyzer and visualizer built with C# and Windows Forms."
permalink: /documentation/
---

Browse the published guides, examples and reference pages for **Spectrum Audio Analyzer**. Each guide has a readable web page; the Markdown sources remain available on GitHub.

## Spectrum operating and engineering guides

- [Application features, modes, controls, Windows requirements and reproducible build instructions]({{ '/' | relative_url }})
- [Exact-version third-party notices, terms and provenance]({{ site.baseurl }}{% link THIRD_PARTY_NOTICES.md %})

Spectrum captures the current Windows render endpoint through BASS/BassWASAPI loopback. It does not isolate a Spotify session or manufacture activity when an endpoint is silent or unavailable. Frequency spectra, PCM waveform/levels and historical display views use different measurement conventions; consult the README measurement section before interpreting them as calibrated audio meters.

## Read online

<table>
<thead><tr><th>Guide</th><th>Markdown or HTML source</th></tr></thead>
<tbody>
{% assign guides = site.html_pages | sort: 'url' %}
{% for guide in guides %}{% if guide.sitemap != false and guide.url != page.url and guide.url != '/404.html' %}
<tr><td><a href="{{ guide.url | absolute_url }}">{{ guide.title | default: guide.name | escape }}</a></td><td><a href="https://github.com/{{ site.repository }}/blob/main/{{ guide.path | uri_escape }}">View source</a></td></tr>
{% endif %}{% endfor %}
{% for guide in site.static_files %}{% if guide.extname == '.html' and guide.sitemap != false %}
<tr><td><a href="{{ guide.path | replace: '/index.html', '/' | absolute_url }}">{% if guide.path == '/Linux_Commands/index.html' %}Linux Command Reference{% elsif guide.path contains 'ai_coding_plugins_beginners_guide' %}AI Coding Add-ons: Beginner’s Guide{% else %}{{ guide.name | escape }} — {{ guide.path | escape }}{% endif %}</a></td><td><a href="https://github.com/{{ site.repository }}/blob/main{{ guide.path | uri_escape }}">View HTML source</a></td></tr>
{% endif %}{% endfor %}
</tbody>
</table>

## Markdown source library

[View this documentation index source](https://github.com/{{ site.repository }}/blob/main/documentation.md). These are the authored Markdown files. Some are package-specific documents or alternate source formats of the same guide.

{% assign sources = site.static_files | sort: 'path' %}
{% for source in sources %}{% if source.extname == '.md' %}
- [{{ source.path | escape }}](https://github.com/{{ site.repository }}/blob/main{{ source.path | uri_escape }})
{% endif %}{% endfor %}

## Explore more

[All developer projects](https://gcfernando.github.io/gcfernando/projects/) · [Repository on GitHub](https://github.com/gcfernando/spectrum-analyzer) · [XML sitemap](https://gcfernando.github.io/spectrum-analyzer/sitemap.xml)
