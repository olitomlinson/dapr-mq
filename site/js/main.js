document.addEventListener('DOMContentLoaded', () => {
  const toggle = document.querySelector('.nav-toggle');
  const links = document.querySelector('.nav-links');
  if (toggle && links) {
    toggle.addEventListener('click', () => links.classList.toggle('open'));
    links.querySelectorAll('a').forEach(a => a.addEventListener('click', () => links.classList.remove('open')));
  }

  document.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      // Leave out full-line comments: they label steps or show expected output, and interactive zsh runs them.
      const code = btn.closest('.code-card').querySelector('pre').textContent
        .split('\n').filter(line => !line.trimStart().startsWith('#')).join('\n')
        .replace(/\n{3,}/g, '\n\n').trim();
      btn.classList.add('used');
      navigator.clipboard.writeText(code).then(() => {
        const original = btn.textContent;
        btn.textContent = 'Copied!';
        setTimeout(() => { btn.textContent = original; }, 1500);
      });
    });
  });

  const storeInput = document.getElementById('statestore-name');
  const nsInput = document.getElementById('statestore-namespace');
  const storeOut = document.getElementById('statestore-name-out');
  const existingPanel = document.getElementById('panel-existing');
  const storePlaceholder = storeOut ? storeOut.textContent : '';
  // The empty-cluster path always installs into daprmq; only the existing-cluster path takes a namespace.
  const renderInstall = () => {
    if (!storeInput || !nsInput || !storeOut) return;
    storeOut.textContent = storeInput.value.trim() || storePlaceholder;
    const ns = (!existingPanel.hidden && nsInput.value.trim()) || 'daprmq';
    document.querySelectorAll('.ns-out').forEach(el => { el.textContent = ns; });
  };
  [storeInput, nsInput].forEach(input => input && input.addEventListener('input', renderInstall));

  const paste = document.getElementById('statestore-paste');
  const picks = document.getElementById('statestore-picks');
  const step3 = document.getElementById('statestore-step3');
  if (paste && picks && step3) {
    paste.addEventListener('input', () => {
      picks.replaceChildren();
      paste.classList.remove('is-valid', 'is-invalid');
      step3.hidden = true;
      if (!paste.value.trim()) return;
      const rows = paste.value.split('\n')
        .map(line => line.trim().split(/\s+/))
        .filter(cols => cols.length >= 4 && cols[0] !== '#' && ['yes', 'no'].includes(cols.at(-1)));
      const stores = rows.filter(cols => cols.at(-1) === 'yes');
      const unsuitable = rows.filter(cols => cols.at(-1) === 'no');
      paste.classList.add(stores.length ? 'is-valid' : 'is-invalid');
      step3.hidden = !stores.length;
      if (!stores.length) {
        const none = document.createElement('p');
        none.textContent = 'No actor state stores in that output. daprMQ needs one marked yes.';
        picks.append(none);
      }
      const row = (ns, name, type) => {
        const pick = document.createElement('div');
        pick.className = 'pick';
        pick.innerHTML = '<span></span><small></small>';
        pick.children[0].textContent = `${ns} / ${name}`;
        pick.children[1].textContent = type;
        picks.append(pick);
        return pick;
      };
      stores.forEach(([ns, name, type]) => {
        const use = document.createElement('button');
        use.type = 'button';
        use.className = 'pick-use';
        use.textContent = 'Use this';
        use.setAttribute('aria-label', `Use ${name} in namespace ${ns}`);
        use.addEventListener('click', () => {
          storeInput.value = name;
          nsInput.value = ns;
          renderInstall();
          const item = document.querySelector('[data-dialog="statestore-help"]').closest('li');
          item.querySelector('input[type="checkbox"]').checked = true;
          paste.closest('dialog').close();
        });
        row(ns, name, type).append(use);
      });
      unsuitable.forEach(([ns, name, type]) => {
        const pill = document.createElement('span');
        pill.className = 'pick-pill';
        pill.textContent = 'Unsuitable';
        pill.title = 'Not an actor state store (actorStateStore is not "true")';
        const pick = row(ns, name, type);
        pick.classList.add('is-unsuitable');
        pick.append(pill);
      });
    });
  }

  const tabs = [...document.querySelectorAll('.install-tabs [role="tab"]')];
  const selectTab = tab => {
    tabs.forEach(t => {
      const on = t === tab;
      t.setAttribute('aria-selected', String(on));
      t.tabIndex = on ? 0 : -1;
      document.getElementById(t.getAttribute('aria-controls')).hidden = !on;
    });
    renderInstall();
  };
  tabs.forEach((tab, i) => {
    tab.addEventListener('click', () => selectTab(tab));
    tab.addEventListener('keydown', e => {
      const step = { ArrowRight: 1, ArrowLeft: -1 }[e.key];
      if (!step) return;
      const next = tabs[(i + step + tabs.length) % tabs.length];
      selectTab(next);
      next.focus();
    });
  });

  document.querySelectorAll('.help-link[data-dialog]').forEach(link => {
    const dialog = document.getElementById(link.dataset.dialog);
    if (!dialog) return;
    link.addEventListener('click', () => dialog.showModal());
    dialog.querySelector('.help-close').addEventListener('click', () => dialog.close());
    dialog.addEventListener('click', e => { if (e.target === dialog) dialog.close(); });
  });

  const cards = document.querySelectorAll('.feature-card');
  const setOpen = (card, open) => {
    card.classList.toggle('open', open);
    card.setAttribute('aria-expanded', String(open));
  };
  cards.forEach(card => {
    card.addEventListener('click', e => {
      if (e.target.closest('.feature-more a')) return;
      if (card.classList.contains('open') && e.target.closest('.feature-more')) return;
      const open = !card.classList.contains('open');
      cards.forEach(c => setOpen(c, false));
      setOpen(card, open);
      if (open) card.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    });
    card.addEventListener('keydown', e => {
      if (e.target !== card) return;
      if (e.key === 'Enter' || e.key === ' ') { e.preventDefault(); card.click(); }
      if (e.key === 'Escape') setOpen(card, false);
    });
  });
});
