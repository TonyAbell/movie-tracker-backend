# Movie Tracker Backend

## Purpose

This project serves as the backend for a Movie Catalog application, providing a chat-based interface for movie search and management. This is a semantic chat interface over The Movie DB Rest API.

The Movie DB Rest API offers various endpoints for searching:
- **Genre List**: [Genre Movie List](https://developer.themoviedb.org/reference/genre-movie-list)
- **Search Person**: [Search Person](https://developer.themoviedb.org/reference/search-person)
- **Search Movie**: [Search Movie](https://developer.themoviedb.org/reference/search-movie)

A user may want to perform a complex query such as:
- "What action movies in the 90s did the main actor also star in a comedy with Meg Ryan in the 80s?"
- "List all sci-fi movies directed by Steven Spielberg that were released after 2000."
- "Find movies where Leonardo DiCaprio and Kate Winslet acted together, in the last 10 years"
- "Find movies in the 'Adventure' genre that were released in the 1980s that had low ratings starring Tom Cruise."
- "What genre of movies did Tom Hanks act in the 90s?"
- "Show me horror movies released in the last five years with a rating above 7.0, staring any one who costared with Johnny Depp in the 2000s."
- "Find movies where Tom Hanks and Julia Roberts both appear that were released in the 90s and where comedies"

These queries would require several sub-queries or a very complex UI to express with UI controls. Enabling a semantic chat interface allows the user to express their intent naturally, allowing the chat agent to construct the necessary queries and present the results in a clear and concise response.

## Table of Contents

- [Infrastructure](#infrastructure)
- [How to Use](#how-to-use)
- [Prompts](#prompts)


## Infrastructure

The infrastructure for the Movie Tracker Backend is managed using Bicep scripts, which automate the deployment of the necessary Azure resources.

### Deployment Instructions

1. **Install Azure CLI**: Ensure you have the Azure CLI installed on your machine. You can download it from [Azure CLI](https://docs.microsoft.com/en-us/cli/azure/install-azure-cli).
2. **Login to Azure**: Run `az login` to authenticate your Azure account.
3. **Navigate to the Infrastructure Directory**: 
    ```sh
    cd infrastructure
    ```
4. **Obtain API Keys**:
    - **OpenAI API Key**: Sign up or log in to [OpenAI](https://beta.openai.com/signup/) and generate an API key.
    - **The Movie Database (TMDb) API Key**: Follow the instructions on [The Movie Database Getting Started Guide](https://developer.themoviedb.org/docs/getting-started) to sign up and obtain an API key.
5. **Configure Principal IDs**: The `adminPrincipalIds` parameter is used to grant Azure Entra RBAC access to the created resources. This should include the object IDs of the users or service principals who will have administrative access.
    - To get your Azure Entra object ID, run the following command:
        ```sh
        az ad user show --id <your-email> --query objectId --output tsv
        ```
    - Replace `<your-email>`  with the actual values. Collect these object IDs into a list format like `["<objectId1>"]`.
6. **Deploy Resources**: Execute the Bicep scripts to deploy the Azure resources.
    ```sh
    az deployment group create --name MyDeployment --resource-group movie-tracker-rg --template-file ./main.bicep --parameters adminPrincipalIds="['<objectId1>']" open_ai_api_key='<yourOpenAIKey>' the_movie_db_api_key='<yourMovieDbKey>'
    ```

By following these instructions, you will set up the necessary infrastructure for the Movie Tracker Backend, including obtaining and configuring the required API keys for OpenAI and The Movie Database, and setting up Azure Entra RBAC access.



## How to Use
This section provides an overview of the API calls and basic usage of the Movie Tracker Backend.

### API Endpoints

The Movie Tracker Backend uses a chat-based interface for querying the movie catalog. Below is a step-by-step guide on how to interact with the API.

#### 1. Start a Chat Session
To begin any interaction, you first need to initialize a chat session. This session will be used to maintain context for subsequent queries.

- **Endpoint**: `GET /api/chat/start`
- **Response**:
    ```json
    {
        "chatId": "Xl9cJSE"
    }
    ```
  
  When you hit this endpoint, it returns a `chatId`, which is a unique identifier for your chat session. You'll use this `chatId` to reference the session in future API calls.

#### 2. Ask a Question
Once you have a chat session initialized, you can ask questions within this session. This is done by sending a POST request to the `/api/chat/{chatId}/ask` endpoint.

- **Endpoint**: `POST /api/chat/{chatId}/ask`
- **Path Parameters**:
    - `chatId`: The ID of the chat session obtained from the previous step.
- **Request Body**:
    ```json
    {
        "Input": "what movies did Jonathan Pryce do in the mid 80s?"
    }
    ```
  
  The `Input` field should contain your query. This example asks for movies featuring Jonathan Pryce from the mid-80s.

- **Response**:
    ```json
    {
        "messages": [
            {
                "role": "user",
                "text": "what movies did Jonathan Pryce do in the mid 80s?"
            },
            {
                "role": "assistant",
                "text": "{\n  \"SystemMessage\": \"Here is the list of movies featuring Jonathan Pryce in the mid-80s:\",\n  \"MovieList\": [\n    {\n      \"MovieId\": \"80368\",\n      \"MovieName\": \"The Doctor and the Devils\"\n    },\n    {\n      \"MovieId\": \"68\",\n      \"MovieName\": \"Brazil\"\n    }\n  ]\n}"
            }
        ]
    }
    ```

 The response includes the chat history for the session. Each message in the history is labeled with a role (either `user` for queries or `assistant` for responses). The assistant's response is a stringified JSON object with the following structure:

```json
{
  "SystemMessage": "Here is the list of movies featuring Jonathan Pryce in the mid-80s:",
  "MovieList": [
    {
      "MovieId": "80368",
      "MovieName": "The Doctor and the Devils"
    },
    {
      "MovieId": "68",
      "MovieName": "Brazil"
    }
  ]
}
```

This structure is designed to drive the UI for the chat front end. The `SystemMessage` field contains a human-friendly message that can be displayed in the chat window to inform the user about the results of their query. Meanwhile, the `MovieList` field contains a computer-friendly list of movies that can be processed and displayed elsewhere on the UI, such as in a list or grid format.

For example:

- The `SystemMessage` ("Here is the list of movies featuring Jonathan Pryce in the mid-80s:") would appear in the chat conversation, making it clear to the user what information is being presented.
- The `MovieList`, which is an array of movie objects, can be iterated over to dynamically generate elements on the web page, such as movie cards or list items, showing the `MovieName` for each movie and possibly using the `MovieId` to link to more detailed information about each film.

By structuring the response this way, the backend provides both clear communication for users and structured data for the UI to render, facilitating a seamless and informative user experience.


### Basic Usage
1. **Clone the Repository**:
    ```sh
    git clone https://github.com/TonyAbell/movie-tracker-backend
    ```
2. **Navigate to the Project Directory**:
    ```sh
    cd movie-tracker-backend/src/MovieTracker.Backend
    ```
3. **Install Azure Functions Core Tools**: Ensure you have Azure Functions Core Tools installed on your machine. You can download it from [Azure Functions Core Tools](https://docs.microsoft.com/en-us/azure/azure-functions/functions-run-local#v2).
4. **Start the Azure Function**:
    ```sh
    func start
    ```
## Prompts
The following are the Semantic Kernel prompts used to drive the semantic movie search functionality.

1. **Get Genres List**:
    ```text
    Get the list of official genres for movies.
    ```

    Example Response:
    ```json
    [
        {
            "GenreId": "28",
            "GenreName": "Action"
        },
        {
            "GenreId": "12",
            "GenreName": "Adventure"
        },
        ...
    ]
    ```

2. **Search for People**:
    ```text
    Search for people by their name and also known as names. The name of the person is "{personName}".
    ```

    Example Response:
    ```json
    [
        {
            "PersonId": "12835",
            "PersonName": "Leonardo DiCaprio"
        },
        {
            "PersonId": "287",
            "PersonName": "Brad Pitt"
        },
        ...
    ]
    ```

3. **Get Person's Movie Credits**:
    ```text
    Get the top few movie credits for a person by their id. The Id for the person is "{personId}". Filter movies by date if necessary: greater than "{greaterThanDate}" and less than "{lessThanDate}".
    ```

    Example Response:
    ```json
    [
        {
            "MovieId": "603",
            "MovieName": "The Matrix"
        },
        {
            "MovieId": "604",
            "MovieName": "The Matrix Reloaded"
        },
        ...
    ]
    ```

These prompts are designed to interact with the backend API to fetch relevant movie information. The responses are structured in JSON format to facilitate easy processing and display in a user interface.
