/**
 * Copie du texte dans le presse-papiers.
 *
 * L'API asynchrone n'est disponible qu'en contexte sécurisé ; le repli par `<textarea>` couvre le
 * cas où la console est servie en clair sur une adresse autre que localhost — c'est-à-dire un
 * déploiement d'intégration, là où copier un identifiant est justement le plus utile.
 */
export async function copyToClipboard(text: string): Promise<boolean> {
  try {
    if (navigator.clipboard && globalThis.isSecureContext) {
      await navigator.clipboard.writeText(text)
      return true
    }
  } catch {
    // Repli ci-dessous.
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
