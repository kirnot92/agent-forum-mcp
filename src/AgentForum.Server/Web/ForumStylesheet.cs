namespace AgentForum.Server.Web;

internal static class ForumStylesheet
{
    public const string Content = """
        :root {
          color-scheme: light;
          --bg: #f7f7f5;
          --surface: #ffffff;
          --text: #252523;
          --muted: #6d6d68;
          --line: #deded9;
          --soft: #efefeb;
          --link: #3e4b56;
          font-family: Inter, ui-sans-serif, system-ui, -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
        }

        * { box-sizing: border-box; }
        body { margin: 0; background: var(--bg); color: var(--text); line-height: 1.6; }
        a { color: var(--link); text-underline-offset: .18em; }
        a:hover { color: #15191c; }
        a:focus-visible, input:focus-visible, button:focus-visible {
          outline: 3px solid #8b969e;
          outline-offset: 2px;
        }

        .site-header { border-bottom: 1px solid var(--line); background: rgba(255,255,255,.88); }
        .header-inner, .page, .site-footer { width: min(100% - 2rem, 760px); margin-inline: auto; }
        .header-inner { min-height: 3.5rem; display: flex; align-items: center; justify-content: space-between; gap: 1rem; }
        .brand { color: var(--text); font-weight: 650; text-decoration: none; letter-spacing: -.01em; }
        nav { display: flex; gap: 1rem; font-size: .9rem; }
        .page { padding-block: 3.5rem 5rem; }
        .site-footer { padding-block: 1.5rem 2.5rem; border-top: 1px solid var(--line); color: var(--muted); font-size: .82rem; }

        h1, h2, h3 { line-height: 1.25; letter-spacing: -.02em; }
        h1 { margin: 0 0 1rem; font-size: clamp(1.85rem, 5vw, 2.55rem); font-weight: 650; }
        h2 { margin: 2.75rem 0 1rem; font-size: 1.2rem; font-weight: 650; }
        h3 { margin: 0; font-size: 1.03rem; }
        p { margin: .55rem 0; }
        .lede { max-width: 66ch; color: #4f4f4b; font-size: 1.02rem; }
        .eyebrow, .secondary { color: var(--muted); font-size: .8rem; }
        .eyebrow { margin-bottom: .45rem; text-transform: uppercase; letter-spacing: .065em; font-weight: 650; }
        .mono, code, time { font-family: ui-monospace, SFMono-Regular, Consolas, "Liberation Mono", monospace; font-size: .92em; }
        .prose { white-space: pre-wrap; overflow-wrap: anywhere; }
        .muted { color: var(--muted); }

        .notice, .error-panel { margin: 1.5rem 0 2rem; padding: 1rem 1.1rem; border: 1px solid var(--line); border-radius: .75rem; background: #fafaf8; }
        .notice { color: #4f4f4b; }
        .error-panel h1 { font-size: 1.55rem; }
        .error-code { color: var(--muted); font-family: ui-monospace, monospace; }

        .search-form { margin: 2rem 0; display: grid; grid-template-columns: 1fr 1.4fr auto; gap: .8rem; align-items: end; }
        .field { display: grid; gap: .35rem; }
        label { color: #555550; font-size: .8rem; font-weight: 600; }
        input { width: 100%; min-height: 2.55rem; padding: .55rem .7rem; border: 1px solid #cfcfc9; border-radius: .55rem; background: var(--surface); color: var(--text); font: inherit; }
        button { min-height: 2.55rem; padding: .55rem .9rem; border: 1px solid #3f4549; border-radius: .55rem; background: #3f4549; color: white; font: inherit; cursor: pointer; }

        .post-list, .timeline { margin: 0; padding: 0; list-style: none; }
        .post-card { padding: 1.25rem 0; border-top: 1px solid var(--line); }
        .post-card:last-child { border-bottom: 1px solid var(--line); }
        .post-card-title { display: inline-block; color: var(--text); font-weight: 650; text-decoration-thickness: 1px; }
        .post-card .snippet { margin: .55rem 0 .7rem; color: #494945; }
        .meta-row { display: flex; flex-wrap: wrap; gap: .35rem 1rem; color: var(--muted); font-size: .78rem; }

        .post-body { margin: 1.75rem 0; padding: 1.4rem; border: 1px solid var(--line); border-radius: .8rem; background: var(--surface); }
        .post-body .prose { font-size: 1.02rem; }
        .context-grid, .count-grid { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: .8rem 1.5rem; }
        .context-grid { margin: 1.25rem 0; }
        .context-item dt { color: var(--muted); font-size: .75rem; }
        .context-item dd { margin: .1rem 0 0; overflow-wrap: anywhere; }
        .count-grid { margin: 1.5rem 0; padding: 1rem 0; border-block: 1px solid var(--line); }
        .count { color: #464642; font-size: .82rem; }
        .count strong { display: block; color: var(--text); font-size: 1rem; font-weight: 620; }

        .timeline { border-left: 1px solid var(--line); margin-left: .4rem; padding-left: 1.2rem; }
        .timeline-item { position: relative; margin: 0 0 1rem; padding: 1rem 1.05rem; border: 1px solid var(--line); border-radius: .7rem; background: var(--surface); }
        .timeline-item::before { content: ""; position: absolute; left: -1.55rem; top: 1.35rem; width: .65rem; height: .65rem; border: 2px solid var(--bg); border-radius: 50%; background: #a3a39e; }
        .timeline-head { display: flex; justify-content: space-between; align-items: baseline; gap: 1rem; margin-bottom: .55rem; }
        .badge { display: inline-block; padding: .15rem .45rem; border: 1px solid #c9c9c3; border-radius: 999px; background: #f3f3ef; color: #50504c; font-size: .72rem; font-weight: 650; letter-spacing: .015em; }
        .badge-worked-with-changes { border-style: dashed; }
        .badge-did-not-work { background: #eeeDEA; }

        .empty { padding: 2rem 0; border-block: 1px solid var(--line); color: var(--muted); }

        @media (max-width: 640px) {
          .header-inner, .page, .site-footer { width: min(100% - 1.25rem, 760px); }
          .page { padding-block: 2.25rem 3.5rem; }
          .search-form { grid-template-columns: 1fr; }
          .context-grid { grid-template-columns: 1fr; }
          .count-grid { grid-template-columns: repeat(2, minmax(0, 1fr)); }
          .post-body { padding: 1rem; }
          .timeline-head { display: block; }
          .timeline-head > time { display: block; margin-top: .25rem; }
        }
        """;
}
