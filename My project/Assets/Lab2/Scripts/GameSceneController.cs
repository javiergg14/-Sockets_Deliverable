using TMPro;
using UnityEngine;
using UnityEngine.SceneManagement;

// Escena de juego (S_Game): a donde van todos cuando el host pulsa "Iniciar partida".
// Ponlo en cualquier GameObject de esa escena y enlaza el boton Volver a BackToMenu().
public class GameSceneController : MonoBehaviour
{
    public TextMeshProUGUI titleText;
    public TextMeshProUGUI playersText;

    void Start()
    {
        if (titleText != null) titleText.text = "Partida iniciada";
        if (playersText != null) playersText.text = "Jugadores:\n" + string.Join("\n", LobbyData.PlayerNames);
    }

    // Boton "Volver": el host cierra la sala; un cliente sale y vuelve a la pantalla de unirse
    public void BackToMenu()
    {
        SocketsServerBase server = FindAnyObjectByType<SocketsServerBase>();
        if (server != null)
        {
            string scene = server.createSceneName;
            server.Disconnect();
            Destroy(server.gameObject);
            SceneManager.LoadScene(scene);
            return;
        }

        SocketsClientBase client = FindAnyObjectByType<SocketsClientBase>();
        if (client != null)
        {
            client.LeaveRoom();     // vuelve a la escena de unirse por si solo
            return;
        }

        SceneManager.LoadScene(0);
    }
}