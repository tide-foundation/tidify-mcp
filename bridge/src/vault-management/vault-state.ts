export const VaultState = {
    NotRunning: 0,
    AdaptorRequired: 1,
    CertificateApprovalRequired: 2,
    Running: 3
} as const;
export type VaultState = (typeof VaultState)[keyof typeof VaultState];

export const parseVaultState = (input: string): VaultState => {
    if (!Object.hasOwn(VaultState, input)) throw new Error(`Invalid vault state: ${input}`);
    return VaultState[input as keyof typeof VaultState];
};

let current: VaultState = VaultState.NotRunning;
export const getVaultState = (): VaultState => current;
export const setVaultState = (s: VaultState): void => { current = s; };