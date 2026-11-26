// This application uses only the React 19 client root entry point.
// The renderer is supplied by the independently locked react-dom package.
declare module 'react-dom/client' {
  import type {ReactNode} from 'react';
  export interface Root {
    render(children: ReactNode): void;
    unmount(): void;
  }
  export function createRoot(container: Element | Document | DocumentFragment): Root;
}
