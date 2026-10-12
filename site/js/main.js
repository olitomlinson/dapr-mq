document.addEventListener('DOMContentLoaded', () => {
  const toggle = document.querySelector('.nav-toggle');
  const links = document.querySelector('.nav-links');
  if (toggle && links) {
    toggle.addEventListener('click', () => links.classList.toggle('open'));
    links.querySelectorAll('a').forEach(a => a.addEventListener('click', () => links.classList.remove('open')));
  }

  document.querySelectorAll('.copy-btn').forEach(btn => {
    btn.addEventListener('click', () => {
      const code = btn.closest('.code-card').querySelector('pre').textContent;
      navigator.clipboard.writeText(code).then(() => {
        const original = btn.textContent;
        btn.textContent = 'Copied!';
        setTimeout(() => { btn.textContent = original; }, 1500);
      });
    });
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
