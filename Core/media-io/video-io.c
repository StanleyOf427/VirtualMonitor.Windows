#include <assert.h>
#include <inttypes.h>
#include "../util/bmem.h"
#include "../util/platform.h"
#include "../util/profiler.h"
#include "../util/threading.h"
#include "../util/darray.h"
#include "../util/util_uint64.h"

#include "format-conversion.h"
#include "video-io.h"
#include "video-frame.h"
#include "video-scaler.h"

extern profiler_name_store_t* obs_get_profiler_name_store(void);

#define MAX_CONVERT_BUFFERS 3
#define MAX_CACHE_SIZE 16

struct cached_frame_info {
	struct video_data frame;
	int skipped;
	int count;
};

struct video_input {
	struct video_scale_info conversion;
	video_scaler_t* scaler;
	struct video_frame frame[MAX_CONVERT_BUFFERS];
	int cur_frame;

	void (*callback)(void* param, struct video_data* frame);
	void* param;
};


struct video_output {
	struct video_output_info info;

	pthread_t thread;
	pthread_mutex_t data_mutex;
	bool stop;

	os_sem_t* update_semaphore;
	uint64_t frame_time;
	volatile long skipped_frames;
	volatile long total_frames;

	bool initialized;

	pthread_mutex_t input_mutex;
	DARRAY(struct video_input) inputs;

	size_t available_frames;
	size_t first_added;
	size_t last_added;
	struct cached_frame_info cache[MAX_CACHE_SIZE];

	volatile bool raw_active;
	volatile long gpu_refs;
}; 
/* ------------------------------------------------------------------------- */

static inline bool scale_video_output(struct video_input* input,
	struct video_data* data)
{
	bool success = true;

	if (input->scaler) {
		struct video_frame* frame;

		if (++input->cur_frame == MAX_CONVERT_BUFFERS)
			input->cur_frame = 0;

		frame = &input->frame[input->cur_frame];

		success = video_scaler_scale(input->scaler, frame->data,
			frame->linesize,
			(const uint8_t* const*)data->data,
			data->linesize);

		if (success) {
			for (size_t i = 0; i < MAX_AV_PLANES; i++) {
				data->data[i] = frame->data[i];
				data->linesize[i] = frame->linesize[i];
			}
		}
		else {
			blog(LOG_WARNING, "video-io: Could not scale frame!");
		}
	}

	return success;
}

static inline bool video_output_cur_frame(struct video_output* video)
{
	struct cached_frame_info* frame_info;
	bool complete;
	bool skipped;

	/* -------------------------------- */

	pthread_mutex_lock(&video->data_mutex);

	frame_info = &video->cache[video->first_added];

	pthread_mutex_unlock(&video->data_mutex);

	/* -------------------------------- */

	pthread_mutex_lock(&video->input_mutex);

	for (size_t i = 0; i < video->inputs.num; i++) {
		struct video_input* input = video->inputs.array + i;
		struct video_data frame = frame_info->frame;

		if (scale_video_output(input, &frame))
			input->callback(input->param, &frame);
	}

	pthread_mutex_unlock(&video->input_mutex);

	/* -------------------------------- */

	pthread_mutex_lock(&video->data_mutex);

	frame_info->frame.timestamp += video->frame_time;
	complete = --frame_info->count == 0;
	skipped = frame_info->skipped > 0;

	if (complete) {
		if (++video->first_added == video->info.cache_size)
			video->first_added = 0;

		if (++video->available_frames == video->info.cache_size)
			video->last_added = video->first_added;
	}
	else if (skipped) {
		--frame_info->skipped;
		os_atomic_inc_long(&video->skipped_frames);
	}

	pthread_mutex_unlock(&video->data_mutex);

	/* -------------------------------- */

	return complete;
}

static void* video_thread(void* param)
{
	struct video_output* video = param;

	os_set_thread_name("video-io: video thread");

	const char* video_thread_name =
		profile_store_name(obs_get_profiler_name_store(),
			"video_thread(%s)", video->info.name);

	while (os_sem_wait(video->update_semaphore) == 0) {
		if (video->stop)
			break;

		profile_start(video_thread_name);
		while (!video->stop && !video_output_cur_frame(video)) {
			os_atomic_inc_long(&video->total_frames);
		}

		os_atomic_inc_long(&video->total_frames);
		profile_end(video_thread_name);

		profile_reenable_thread();
	}

	return NULL;
} 
/* ------------------------------------------------------------------------- */

int video_output_open(video_t** video, struct video_output_info* info)
{
	struct video_output* out;
	pthread_mutexattr_t attr;

	if (!valid_video_params(info))
		return VIDEO_OUTPUT_INVALIDPARAM;

	out = bzalloc(sizeof(struct video_output));
	if (!out)
		goto fail;

	memcpy(&out->info, info, sizeof(struct video_output_info));
	out->frame_time =
		util_mul_div64(1000000000ULL, info->fps_den, info->fps_num);
	out->initialized = false;

	if (pthread_mutexattr_init(&attr) != 0)
		goto fail;
	if (pthread_mutexattr_settype(&attr, PTHREAD_MUTEX_RECURSIVE) != 0)
		goto fail;
	if (pthread_mutex_init(&out->data_mutex, &attr) != 0)
		goto fail;
	if (pthread_mutex_init(&out->input_mutex, &attr) != 0)
		goto fail;
	if (os_sem_init(&out->update_semaphore, 0) != 0)
		goto fail;
	if (pthread_create(&out->thread, NULL, video_thread, out) != 0)
		goto fail;

	init_cache(out);

	out->initialized = true;
	*video = out;
	return VIDEO_OUTPUT_SUCCESS;

fail:
	video_output_close(out);
	return VIDEO_OUTPUT_FAIL;
}

void video_output_close(video_t* video)
{
	if (!video)
		return;

	video_output_stop(video);

	for (size_t i = 0; i < video->inputs.num; i++)
		video_input_free(&video->inputs.array[i]);
	da_free(video->inputs);

	for (size_t i = 0; i < video->info.cache_size; i++)
		video_frame_free((struct video_frame*)&video->cache[i]);

	os_sem_destroy(video->update_semaphore);
	pthread_mutex_destroy(&video->data_mutex);
	pthread_mutex_destroy(&video->input_mutex);
	bfree(video);
}


static inline bool video_input_init(struct video_input* input,
	struct video_output* video)
{
	if (input->conversion.width != video->info.width ||
		input->conversion.height != video->info.height ||
		input->conversion.format != video->info.format) {
		struct video_scale_info from = { .format = video->info.format,
						.width = video->info.width,
						.height = video->info.height,
						.range = video->info.range,
						.colorspace =
							video->info.colorspace };

		int ret = video_scaler_create(&input->scaler,
			&input->conversion, &from,
			VIDEO_SCALE_FAST_BILINEAR);
		if (ret != VIDEO_SCALER_SUCCESS) {
			if (ret == VIDEO_SCALER_BAD_CONVERSION)
				blog(LOG_ERROR, "video_input_init: Bad "
					"scale conversion type");
			else
				blog(LOG_ERROR, "video_input_init: Failed to "
					"create scaler");

			return false;
		}

		for (size_t i = 0; i < MAX_CONVERT_BUFFERS; i++)
			video_frame_init(&input->frame[i],
				input->conversion.format,
				input->conversion.width,
				input->conversion.height);
	}

	return true;
}

bool video_output_connect(
	video_t* video, const struct video_scale_info* conversion,
	void (*callback)(void* param, struct video_data* frame), void* param)
{
	bool success = false;

	if (!video || !callback)
		return false;

	pthread_mutex_lock(&video->input_mutex);

	if (video_get_input_idx(video, callback, param) == DARRAY_INVALID) {
		struct video_input input;
		memset(&input, 0, sizeof(input));

		input.callback = callback;
		input.param = param;

		if (conversion) {
			input.conversion = *conversion;
		}
		else {
			input.conversion.format = video->info.format;
			input.conversion.width = video->info.width;
			input.conversion.height = video->info.height;
		}

		if (input.conversion.width == 0)
			input.conversion.width = video->info.width;
		if (input.conversion.height == 0)
			input.conversion.height = video->info.height;

		success = video_input_init(&input, video);
		if (success) {
			if (video->inputs.num == 0) {
				if (!os_atomic_load_long(&video->gpu_refs)) {
					reset_frames(video);
				}
				os_atomic_set_bool(&video->raw_active, true);
			}
			da_push_back(video->inputs, &input);
		}
	}

	pthread_mutex_unlock(&video->input_mutex);

	return success;
}


bool video_output_active(const video_t* video)
{
	if (!video)
		return false;
	return os_atomic_load_bool(&video->raw_active);
}

const struct video_output_info* video_output_get_info(const video_t* video)
{
	return video ? &video->info : NULL;
}

uint64_t video_output_get_frame_time(const video_t* video)
{
	return video ? video->frame_time : 0;
}

void video_output_stop(video_t* video)
{
	void* thread_ret;

	if (!video)
		return;

	if (video->initialized) {
		video->initialized = false;
		video->stop = true;
		os_sem_post(video->update_semaphore);
		pthread_join(video->thread, &thread_ret);
	}
}

bool video_output_stopped(video_t* video)
{
	if (!video)
		return true;

	return video->stop;
}

enum video_format video_output_get_format(const video_t* video)
{
	return video ? video->info.format : VIDEO_FORMAT_NONE;
}

uint32_t video_output_get_width(const video_t* video)
{
	return video ? video->info.width : 0;
}

uint32_t video_output_get_height(const video_t* video)
{
	return video ? video->info.height : 0;
}

double video_output_get_frame_rate(const video_t* video)
{
	if (!video)
		return 0.0;

	return (double)video->info.fps_num / (double)video->info.fps_den;
}
