declare module '*.webp' {
  const url: string;
  export default url;
}

declare module '*.ogg' {
  const bytes: Uint8Array<ArrayBuffer>;
  export default bytes;
}

declare const PRO3_HOME_ART: { left: string; right: string; case: string; caseCharging: string } | null;
type HardwareArtwork = { left?: string; right?: string; case?: string; caseCharging?: string; headphone?: string };
declare const AIRPODS_MODEL_ART: Record<string, HardwareArtwork>;
declare const AIRPODS_LOCAL_ART: Record<string, HardwareArtwork> | null;
declare const OWNER_PREVIEW: boolean;
declare const AIRPODS_CONNECT_MOTION: Record<string, string> | null;
type WaitingMotionAssets = { intro: string; loop: string; poster: string; cue?: number | null; aligned?: boolean };
declare const AIRPODS_WAITING_MOTION: Record<string, WaitingMotionAssets> | null;
declare const AIRPODS_HOME_POSTERS: Record<string, string> | null;
type PopupScene = { video: string; poster: string };
type PopupModelArt = { layout: 'split' | 'single'; standard: PopupScene; charging?: PopupScene };
declare const AIRPODS_LOCAL_POPUPS: Record<string, PopupModelArt> | null;
