export type DashboardMode = 'queue' | 'topic';

export const getInitialMode = (): DashboardMode => {
  const params = new URLSearchParams(window.location.search);
  return params.get('mode') === 'topic' ? 'topic' : 'queue';
};

export const updateModeInUrl = (mode: DashboardMode): void => {
  const url = new URL(window.location.href);
  url.searchParams.set('mode', mode);
  window.history.pushState({}, '', url.toString());
};
