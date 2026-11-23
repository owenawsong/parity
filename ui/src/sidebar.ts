export const sidebarPages = [
  { page: 'audioMotion', label: 'nav.sound', icon: 'sound' },
  { page: 'pulse', label: 'nav.pulse', icon: 'pulse' },
  { page: 'controls', label: 'hub.gestures', icon: 'controls' },
  { page: 'micSetup', label: 'nav.microphone', icon: 'microphone' },
] as const;

export function orderedPages(saved: readonly string[]) {
  const result = [...sidebarPages];
  const rank = (page: string) => {
    const index = saved.indexOf(page);
    return index < 0 ? saved.length : index;
  };
  return result.sort((a, b) => rank(a.page) - rank(b.page));
}
