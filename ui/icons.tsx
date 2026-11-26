import React from 'react';

const paths = {
  grid: 'M3 3.5h7v7H3z M14 3.5h7v7h-7z M3 14h7v7H3z M14 14h7v7h-7z',
  stack: 'M3 7l9-4 9 4-9 4z M3 12l9 4 9-4 M3 17l9 4 9-4',
  photo: 'M4 4h16v16H4z M4.5 16l5-5 4 4 2.5-3 4 4 M15.5 8h.01',
  photos: 'M7 7h14v14H7z M17 3H3v14 M8 17l4-4 3 3 2-2 3 3 M16 10h.01',
  sliders: 'M4 5h4m4 0h8 M4 12h10m4 0h2 M4 19h2m4 0h10 M8 3v4h4V3z M14 10v4h4v-4z M6 17v4h4v-4z',
  star: 'M12 3l2.7 5.5 6.1.9-4.4 4.3 1 6.1L12 17l-5.4 2.8 1-6.1-4.4-4.3 6.1-.9z',
  heart: 'M12 20S3 14.4 3 8.8a4.7 4.7 0 019-1.6 4.7 4.7 0 019 1.6c0 5.6-9 11.2-9 11.2z',
  folder: 'M3 6h6l2 2h10v11H3z',
  plus: 'M12 5v14 M5 12h14',
  minus: 'M5 12h14',
  chevron: 'M8 10l4 4 4-4',
  right: 'M9 5l7 7-7 7',
  left: 'M15 5l-7 7 7 7',
  close: 'M6 6l12 12 M18 6L6 18',
  check: 'M4 12l5 5L20 6',
  checkCircle: 'M21 11v1a9 9 0 11-5.3-8.2 M8 11l4 4 9-10',
  undo: 'M8 4L3 9l5 5 M3 9h11a6 6 0 010 12',
  redo: 'M16 4l5 5-5 5 M21 9H10a6 6 0 000 12',
  reset: 'M3 10a9 9 0 118 11 M3 4v6h6',
  history: 'M3 11a9 9 0 119 10 M3 4v7h6 M12 7v5l3 2',
  collapseL: 'M3 4h18v16H3z M9 4v16 M16 9l-3 3 3 3',
  collapseR: 'M3 4h18v16H3z M15 4v16 M8 9l3 3-3 3',
  fit: 'M3 9V3h6 M15 3h6v6 M21 15v6h-6 M9 21H3v-6',
  compare: 'M3 5h18v14H3z M12 2v20 M6 9v6',
  export: 'M12 14V3 M7.5 7.5L12 3l4.5 4.5 M4 13v7h16v-7',
  import: 'M12 3v11 M7.5 9.5L12 14l4.5-4.5 M4 14v6h16v-6',
  archive: 'M3 4h18v5H3z M5 9v11h14V9 M9 13h6',
  film: 'M3 4h18v16H3z M7 4v16 M17 4v16 M3 9h4 M3 15h4 M17 9h4 M17 15h4',
  sparkle: 'M12 3l1.9 5.5L19 11l-5.1 2.5L12 19l-2-5.5L5 11l5-2.5z M19 2v4 M17 4h4',
  send: 'M4 11l16-7-6 16-3-7z M11 13l9-9',
  search: 'M16 16l5 5 M18 10.5a7.5 7.5 0 11-15 0 7.5 7.5 0 0115 0',
  lock: 'M5 10h14v11H5z M8 10V7a4 4 0 118 0v3 M12 14v3',
  unlock: 'M5 10h14v11H5z M8 10V7a4 4 0 017.8-1 M12 14v3',
  dots: 'M4 12h.01 M12 12h.01 M20 12h.01',
  sun: 'M16 12a4 4 0 11-8 0 4 4 0 018 0z M12 2v3 M12 19v3 M2 12h3 M19 12h3 M5 5l2 2 M17 17l2 2 M5 19l2-2 M17 7l2-2',
  droplet: 'M12 2S4.5 10 4.5 15a7.5 7.5 0 0015 0C19.5 10 12 2 12 2z M8 15a4 4 0 004 4',
  detail: 'M3 18l6-12 4 7 3-5 5 10z',
  channels: 'M3 20V3 M3 20h18 M5 16c5 0 6-8 9-9 2-1 4 0 6-4',
  keyboard: 'M2 5h20v14H2z M5 9h1m2 0h1m2 0h1m2 0h1m2 0h1 M5 12h1m2 0h1m2 0h1m2 0h1m2 0h1 M8 16h8',
  info: 'M21 12a9 9 0 11-18 0 9 9 0 0118 0z M12 11v5 M12 7h.01',
  copy: 'M8 8h13v13H8z M16 3H3v13',
  save: 'M4 3h13l3 3v15H4z M8 3v6h8V3 M8 14h8v7H8z',
  palette: 'M21 12a9 9 0 10-9 9h1a2 2 0 001.3-3.5 1.5 1.5 0 011.1-2.5h2A3.6 3.6 0 0021 12z M7 10h.01 M10 7h.01 M15 7.5h.01 M17.5 11h.01',
  target: 'M3 9V4h5 M16 4h5v5 M21 15v5h-5 M8 20H3v-5 M12 9v6 M9 12h6',
  eye: 'M2 12s3-7 10-7 10 7 10 7-3 7-10 7S2 12 2 12z M15 12a3 3 0 11-6 0 3 3 0 016 0z',
  list: 'M8 6h13 M8 12h13 M8 18h13 M3 6h.01 M3 12h.01 M3 18h.01',
  settings: 'M4 7h16 M4 17h16 M9 4v6 M15 14v6',
  desktop: 'M3 4h18v13H3z M8 21h8 M12 17v4',
  trash: 'M4 6h16 M9 3h6 M6 6l1 15h10l1-15 M10 10v7 M14 10v7',
  brush: 'M14 13l6-9a1.6 1.6 0 00-2-2l-9 8z M12 15c0 3-3 6-9 5 3-1 2-3 3-5 1-3 4-3 6 0z',
  menu: 'M4 6h16 M4 12h16 M4 18h16',
  hand: 'M8 12V6a1.5 1.5 0 113 0v6-8a1.5 1.5 0 113 0v8-6a1.5 1.5 0 113 0v7-4a1.5 1.5 0 113 0v6c0 4-2 7-6 7h-2c-2 0-4-1-5-3l-4-6a1.6 1.6 0 012.4-2L8 14z',
};
export type IconName = keyof typeof paths;
export function Icon({name, size = 16, className = ''}: {name: IconName; size?: number; className?: string}) {
  return <svg className={'icon ' + className} width={size} height={size} viewBox="0 0 24 24" aria-hidden="true"
    fill="none" stroke="currentColor" strokeWidth={name === 'dots' ? 3.7 : 1.65} strokeLinecap="round" strokeLinejoin="round">
    <path d={paths[name]} />
  </svg>;
}
export function Mark({large = false}: {large?: boolean}) {
  return <svg aria-hidden="true" viewBox="0 0 48 48" className={large ? 'aperture large' : 'aperture'}>
    <g fill="currentColor">
      {[0, 60, 120, 180, 240, 300].map(rotation => <path key={rotation} transform={'rotate(' + rotation + ' 24 24)'} d="M23 3a21 21 0 00-14.7 7.2l12.5 1.3 7.4-8.1z" />)}
    </g>
    <path d="M23.7 10l12 7v14l-12 7-12-7V17z" fill="none" stroke="currentColor" strokeWidth="1.8"/>
  </svg>;
}
