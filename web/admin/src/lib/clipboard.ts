/**
 * Copies text to the clipboard.
 *
 * The async API is only available in a secure context; the `<textarea>` fallback covers the case
 * where the console is served over plain HTTP on an address other than localhost — that is, a
 * staging deployment, exactly where copying an identifier is most useful.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && globalThis.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // Fallback below.
  }

  try {
    const area = document.createElement('textarea')

    area.value = text
    area.setAttribute('readonly', '')
    area.style.position = 'fixed'
    area.style.opacity = '0'
    document.body.appendChild(area)
    area.select()

    const copied = document.execCommand('copy')

    document.body.removeChild(area)
    return copied
  } catch {
    return false
  }
}
