using FFmpeg.AutoGen;
using FFmpeg.Helper;

namespace FFmpegMedia; 

public unsafe class FFmpegMedia
{

	static int VideoPts = 0; //视频pts
	static int OUTPUT_CHANNELS = 2;//双通道
	static int OUTPUT_BIT_RATE = 128000;//比特率
	static int AudioPts = 0;//音频pts
    static object _lock = new object();
	public static unsafe void Start(CancellationToken token)
	{
#if DEBUG
		Console.WriteLine("Current directory: " + Environment.CurrentDirectory);
		Console.WriteLine("Running in {0}-bit mode.", Environment.Is64BitProcess ? "64" : "32");
		Console.WriteLine($"FFmpeg version info: {ffmpeg.av_version_info()}");
		Console.WriteLine();
#endif
		WriteFile(token);
	}

	static unsafe int Open_video_input_file(AVFormatContext** video_input_format_context, string url, ref int videoIndex)
	{

		AVInputFormat* input_format = ffmpeg.av_find_input_format("dshow");
		//video=Integrated Camera:
		int ret = ffmpeg.avformat_open_input(video_input_format_context, url, input_format, null);
		if (ret < 0)
		{
			Console.WriteLine($"Could not open input file '{url}' (error '{FFmpegHelper.av_err2str(ret)}')");
			*video_input_format_context = null;
			return ret;
		}
		if ((ret = ffmpeg.avformat_find_stream_info(*video_input_format_context, null)) < 0)
		{
			Console.WriteLine($"Could not find stream info (error '{FFmpegHelper.av_err2str(ret)}')");
			ffmpeg.avformat_close_input(video_input_format_context);
			return ret;
		}

		for (int i = 0; i < (*video_input_format_context)->nb_streams; i++)
		{
			if ((*video_input_format_context)->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_VIDEO)
			{
				videoIndex = i;
			}
		}
		return 0;

	}
	static unsafe int Open_audio_input_file(AVFormatContext** audio_input_format_context, string url, ref int audioIndex)
	{
		AVInputFormat* input_format = ffmpeg.av_find_input_format("dshow");
		//video=Integrated Camera:
		int ret = ffmpeg.avformat_open_input(audio_input_format_context, url, input_format, null);
		if (ret < 0)
		{
			Console.WriteLine($"Could not open input file '{url}' (error '{FFmpegHelper.av_err2str(ret)}')");
			*audio_input_format_context = null;
			return ret;
		}
		if ((ret = ffmpeg.avformat_find_stream_info(*audio_input_format_context, null)) < 0)
		{
			Console.WriteLine($"Could not find stream info (error '{FFmpegHelper.av_err2str(ret)}')");
			ffmpeg.avformat_close_input(audio_input_format_context);
			return ret;
		}

		for (int i = 0; i < (*audio_input_format_context)->nb_streams; i++)
		{
			if ((*audio_input_format_context)->streams[i]->codecpar->codec_type == AVMediaType.AVMEDIA_TYPE_AUDIO)
			{
				audioIndex = i;
			}
		}
		return 0;
	}
	static unsafe int Init_Codec_Context(AVFormatContext* input_format_context,int streamIndex, AVCodecContext** codec_context)
	{

		int error = 0;
		AVCodec* codec;
		AVCodecParameters* codecpar = input_format_context->streams[streamIndex]->codecpar;
		if ((codec = ffmpeg.avcodec_find_decoder(codecpar->codec_id)) == null)
		{
			Console.WriteLine("Could not find input codec");
			//ffmpeg.avformat_close_input(&input_ctx);
			return ffmpeg.AVERROR_EXIT;
		}

		AVCodecContext* avcctx;
		avcctx = ffmpeg.avcodec_alloc_context3(codec);
		if (avcctx == null)
		{
			Console.WriteLine("Could not allocate a decoding context");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}

		error = ffmpeg.avcodec_parameters_to_context(avcctx, codecpar);
		if (error < 0)
		{
			ffmpeg.avcodec_free_context(&avcctx);
			return error;
		}

		if ((error = ffmpeg.avcodec_open2(avcctx, codec, null)) < 0)
		{
			Console.WriteLine($"Could not open input codec (error '{FFmpegHelper.av_err2str(error)}')");
			ffmpeg.avcodec_free_context(&avcctx);
			return error;
		}
		*codec_context = avcctx;
		return error;
	}

	#region 合并
	public static unsafe void WriteFile(CancellationToken token)
	{
		AVFormatContext* video_input_format_ctx = null;
		AVFormatContext* audio_input_format_ctx = null;

		AVFormatContext* output_format_ctx = null;
		//视频
		AVCodecContext* video_input_codec_ctx = null;
		AVCodecContext* video_output_codec_ctx = null;
		AVStream* out_stream = null;
		//图像转换上下文
		SwsContext* sws_context = null;

		//音频
		AVCodecContext* audio_input_codec_ctx = null;
		AVCodecContext* audio_output_codec_ctx = null;
		AVStream* audio_out_stream = null;
		SwrContext* resample_context = null;
		AVAudioFifo* fifo = null;

		int videoIndex = -1, audioIndex = -1;
		string video = "video=Integrated Camera";
		if (Open_video_input_file(&video_input_format_ctx, video, ref videoIndex) < 0)
		{
			goto cleanup;
		}
		string audio = "audio=麦克风 (Realtek(R) Audio)";
		if (Open_audio_input_file(&audio_input_format_ctx, audio, ref audioIndex) < 0)
		{
			goto cleanup;
		}

		string outfile = @"D:\Desktop\out.mp4";
		int error = 0;
		if ((error = Init_Codec_Context(video_input_format_ctx, 0, &video_input_codec_ctx)) < 0)
		{
			goto cleanup;
		}
		if ((error = Init_Codec_Context(audio_input_format_ctx, 0, &audio_input_codec_ctx)) < 0)
		{
			goto cleanup;
		}

		if ((error = Open_output_file(outfile, &output_format_ctx, &video_output_codec_ctx, &audio_output_codec_ctx, video_input_codec_ctx->coded_width,
		video_input_codec_ctx->height, &out_stream, &audio_out_stream)) < 0)
		{
			goto cleanup;
		}
		if ((error = Init_SwsContext(&video_input_codec_ctx, &video_output_codec_ctx, &sws_context)) < 0)
		{
			Console.WriteLine("Could not initialize the conversion context");
			goto cleanup;
		}
		if (init_resampler(audio_input_codec_ctx, audio_output_codec_ctx, &resample_context) != 0)
		{
			goto cleanup;
		}

		if (init_fifo(&fifo, audio_output_codec_ctx) != 0)
		{
			goto cleanup;
		}
		//写入文件头
		ffmpeg.avformat_write_header(output_format_ctx, null);

		IntPtr video_input_format_ctx_ptr = new IntPtr(video_input_format_ctx);
		IntPtr video_input_codec_ctx_ptr = new IntPtr(video_input_codec_ctx);
	    IntPtr video_output_codec_ctx_ptr= new IntPtr(video_output_codec_ctx) ;
	    IntPtr sws_context_ptr = new IntPtr(sws_context);
	    IntPtr out_stream_ptr= new IntPtr(out_stream);
		IntPtr output_format_ctx_ptr = new IntPtr(&output_format_ctx);

		IntPtr fifo_ptr = new IntPtr(&fifo);
		IntPtr audio_input_format_ctx_ptr = new IntPtr(audio_input_format_ctx); 
		IntPtr audio_input_codec_ctx_ptr = new IntPtr(audio_input_codec_ctx);
		IntPtr audio_output_codec_ctx_ptr = new IntPtr(audio_output_codec_ctx);
		IntPtr resample_context_ptr = new IntPtr(resample_context);

		Task<int> videoTask = Task.Run(() =>WriteVideo(token, video_input_format_ctx_ptr, video_input_codec_ctx_ptr, video_output_codec_ctx_ptr,
	    	sws_context_ptr, out_stream_ptr, output_format_ctx_ptr,videoIndex));
	    Task<int> audioTask = Task.Run(()=>WriteAudio(token,fifo_ptr,audio_input_format_ctx_ptr,audio_input_codec_ctx_ptr,audio_output_codec_ctx_ptr,
		            resample_context_ptr, output_format_ctx_ptr,audioIndex));
		List<Task<int>> tasks = new List<System.Threading.Tasks.Task<int>> {
	     	videoTask,
			audioTask
		};
		Task.WaitAll(tasks.ToArray());
		if (videoTask.Result < 0)
		{
			goto cleanup;
		}
		if (audioTask.Result < 0)
		{
			goto cleanup;
		}
		ffmpeg.av_write_trailer(output_format_ctx);
	cleanup:
		ffmpeg.avformat_free_context(video_input_format_ctx);
		ffmpeg.avformat_free_context(audio_input_format_ctx);
		ffmpeg.avcodec_free_context(&video_input_codec_ctx);
		ffmpeg.avcodec_free_context(&video_output_codec_ctx);
		ffmpeg.avio_closep(&output_format_ctx->pb);
		ffmpeg.avformat_free_context(output_format_ctx);
	}
  
	static unsafe int Open_output_file(string fileName,
			AVFormatContext** output_format_context,
			AVCodecContext** video_output_codec_context,
			AVCodecContext** audio_output_codec_context,
			int width, int height, AVStream** out_stream,
			AVStream** audio_out_stream)
	{

		int ret = 0;
		ret = ffmpeg.avformat_alloc_output_context2(output_format_context, null, null, fileName);
		if (ret < 0)
		{
			Console.WriteLine("Could not allocate output format context");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}
		ret = ffmpeg.avio_open(&(*output_format_context)->pb, fileName, ffmpeg.AVIO_FLAG_WRITE);
		if (ret < 0)
		{
			Console.WriteLine($"Could not open output file '{fileName}' (error '{FFmpegHelper.av_err2str(ret)}')");
			return ret;
		}
		{
			AVCodecContext* avcCtx;
			AVCodec* output_codec;
			AVStream* stream = null;
			output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
			if (output_codec == null)
			{
				Console.WriteLine("Could not find an encoder for 'AV_CODEC_ID_H264'.");
				return ffmpeg.AVERROR_EXIT;
			}

			avcCtx = ffmpeg.avcodec_alloc_context3(output_codec);
			if (avcCtx == null)
			{
				Console.WriteLine("Could not allocate an encoding context.");
				return ffmpeg.AVERROR_EXIT;
			}
			avcCtx->codec_id = AVCodecID.AV_CODEC_ID_H264;
			avcCtx->bit_rate = 2000000;
			avcCtx->width = width;
			avcCtx->height = height;
			AVRational time_base = new AVRational { num = 1, den = 30 };
			avcCtx->time_base = time_base;
			avcCtx->gop_size = 10;
			avcCtx->max_b_frames = 3;
			avcCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
			if (avcCtx->codec_id == AVCodecID.AV_CODEC_ID_H264)
			{
				ffmpeg.av_opt_set(avcCtx->priv_data, "preset", "slow", 0);
				ffmpeg.av_opt_set(avcCtx->priv_data, "tune", "zerolatency", 0);
			}
			if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
			{
				avcCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
			}

			ret = ffmpeg.avcodec_open2(avcCtx, output_codec, null);
			if (ret < 0)
			{
				Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(ret)}')");
				goto cleanup;
			}

			stream = ffmpeg.avformat_new_stream(*output_format_context, output_codec);
			if (stream == null)
			{
				Console.WriteLine("Could not allocate stream.");
				ret = ffmpeg.AVERROR_EXIT;
				goto cleanup;
			}
			//stream->time_base = time_base;
			ret = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avcCtx);
			if (ret < 0)
			{
				Console.WriteLine($"Could not initialize stream parameters (error '{FFmpegHelper.av_err2str(ret)}')");
				goto cleanup;
			}
			*video_output_codec_context = avcCtx;
			*out_stream = stream;
		}
		{
			AVCodec* audio_output_codec;
			if ((audio_output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC)) == null)
			{
				Console.WriteLine("Could not find an AAC encoder");
				goto cleanup;
			}

			AVCodecContext* audio_avctx = ffmpeg.avcodec_alloc_context3(audio_output_codec);
			if (audio_avctx == null)
			{
				Console.WriteLine("Could not allocate an encoding context");
				ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
				goto cleanup;
			}

			audio_avctx->ch_layout.nb_channels = OUTPUT_CHANNELS;
			audio_avctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(OUTPUT_CHANNELS);
			audio_avctx->sample_rate = 44100;
			audio_avctx->sample_fmt = audio_output_codec->sample_fmts[0];
			audio_avctx->bit_rate = OUTPUT_BIT_RATE;
			audio_avctx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

			if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
			{
				audio_avctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
			}

			if ((ret = ffmpeg.avcodec_open2(audio_avctx, audio_output_codec, null)) < 0)
			{
				Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(ret)}')");
				goto cleanup;
			}
			AVStream* audio_stream;
			if ((audio_stream = ffmpeg.avformat_new_stream(*output_format_context, null)) == null)
			{
				Console.WriteLine("Could not create new stream");
				ret = ffmpeg.AVERROR(ffmpeg.ENOMEM);
				goto cleanup;
			}
			ret = ffmpeg.avcodec_parameters_from_context(audio_stream->codecpar, audio_avctx);
			if (ret < 0)
			{
				Console.WriteLine("Could not initialize stream parameters");
				goto cleanup;
			}

			*audio_output_codec_context = audio_avctx;
			*audio_out_stream = audio_stream;
		}

		return ret;

	cleanup:
		ffmpeg.avio_closep(&(*output_format_context)->pb);
		ffmpeg.avformat_free_context(*output_format_context);
		*output_format_context = null;
		return ret < 0 ? ret : ffmpeg.AVERROR_EXIT;
	}

	static unsafe int WriteVideo(CancellationToken token,IntPtr video_input_format_ctx_ptr,IntPtr video_input_codec_ctx_ptr,
	IntPtr video_output_codec_ctx_ptr,IntPtr sws_context_ptr,IntPtr out_stream_ptr,IntPtr output_format_ctx_ptr,
	int streamIndex)
	{
		
		AVFormatContext* video_input_format_ctx = (AVFormatContext*)video_input_format_ctx_ptr;
		AVCodecContext* video_input_codec_ctx = (AVCodecContext*)video_input_codec_ctx_ptr;
		AVCodecContext* video_output_codec_ctx = (AVCodecContext*)video_output_codec_ctx_ptr;
		SwsContext* sws_context = (SwsContext*)sws_context_ptr;
		AVStream* out_stream = (AVStream*)out_stream_ptr;
		AVFormatContext** output_format_ctx = (AVFormatContext**)output_format_ctx_ptr;
		
		int error = 0;
		AVPacket* video_input_packet = ffmpeg.av_packet_alloc();
		AVPacket* video_output_packet = ffmpeg.av_packet_alloc();
		AVFrame* srcFrame = ffmpeg.av_frame_alloc();
		AVFrame* pFrameYUV = ffmpeg.av_frame_alloc();
		// 计算一帧的大小
		int out_buffer_size = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_YUV420P, video_input_codec_ctx->width, video_input_codec_ctx->height, 1);
		byte* out_buffer = (byte*)ffmpeg.av_malloc((ulong)out_buffer_size);
		byte_ptrArray4* ptrFrameData = (byte_ptrArray4*)&pFrameYUV->data;
		int_array4* ptrLineSize = (int_array4*)&pFrameYUV->linesize;
		error = ffmpeg.av_image_fill_arrays(ref *ptrFrameData, ref *ptrLineSize, out_buffer, AVPixelFormat.AV_PIX_FMT_YUV420P,
		video_input_codec_ctx->width, video_input_codec_ctx->height, 1);
		if (error < 0)
		{
			Console.WriteLine("Could not fill image arrays");
			goto cleanup;
		}
		pFrameYUV->format = (int)video_output_codec_ctx->pix_fmt;
		pFrameYUV->width = video_output_codec_ctx->width;
		pFrameYUV->height = video_output_codec_ctx->height;
		while (!token.IsCancellationRequested)
		{
			error = ffmpeg.av_read_frame(video_input_format_ctx, video_input_packet);
			if (error < 0 || error == ffmpeg.AVERROR_EOF)
			{
				break;
			}
			if (video_input_packet->stream_index == streamIndex)
			{
				error = ffmpeg.avcodec_send_packet(video_input_codec_ctx, video_input_packet);
				if (error < 0)
				{
					Console.WriteLine($"avcodec_send_packet error:{FFmpegHelper.av_err2str(error)}");
					break;
				}
				if ((error = ffmpeg.avcodec_receive_frame(video_input_codec_ctx, srcFrame)) < 0)
				{
					if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
					{
						break;
					}
					else
						if (error < 0)
					{
						break;
					}
				}
				error = ffmpeg.sws_scale(sws_context, (srcFrame)->data, (srcFrame)->linesize, 0,
				(video_input_codec_ctx)->height, (pFrameYUV)->data, (pFrameYUV)->linesize);
				if (error < 0)
				{
					Console.WriteLine("Could not scale the frame");
					break;
				}
				pFrameYUV->pts = srcFrame->pts;
				if ((error = ffmpeg.avcodec_send_frame(video_output_codec_ctx, pFrameYUV)) < 0)
				{
					Console.WriteLine($"avcodec_send_frame error:{FFmpegHelper.av_err2str(error)}");
					break;
				}
				if ((error = ffmpeg.avcodec_receive_packet(video_output_codec_ctx, video_output_packet)) < 0)
				{
					if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
					{
					}
					else
					{
						Console.WriteLine($"avcodec_receive_packet error:{FFmpegHelper.av_err2str(error)}");
						break;
					}
				}
				else
				{
					//在FFMPEG中可以区分 视频流 和 音频流。
					//同时每个媒体流 都有一个时间基数，
					//一般的MP4 的视频流的时间基数是90000，
					//这个时间基数 在封装的时候可以自己定义，
					//这个时间基数可以这么理解，
					//一把尺子一厘米包含了10个小刻度，
					//同理，时间基数 是指 1秒钟 包含了 90000 个 刻度，
					//如果是固定帧率的情况下，假设 设计的视频流 的帧率是 15，
					//那么一帧视频 就 占用了 90000 / 15 = 6000 个时间刻度，
					//那么FFMPEG 中 视频流的时间戳的计算 pts = frame_no（当前帧数） *6000，
					//如果 数据中没有 B帧 数据，那么可以认为 dts 和 pts 相同，如果是 可变帧率 ，
					//就需要统计每秒的帧数，来计算 一帧 数据占用的刻度数，由于 每一帧占的刻度数不一样了，
					//不能简单的 用帧数 *刻度数，而是 使用累加 计算 pts += 90000 / 帧数，dts 和 pts 相同。
					// 将 packet 中的各时间值从输入流封装格式时间基转换到输出流封装格式时间基
					//outPkt->pts = outPkt->dts = frameCount *  (out_stream->time_base.den / 30);
					//frameCount++;
					//Console.WriteLine(outPkt->pts);

					video_output_packet->pts = video_output_packet->dts = VideoPts;
					VideoPts += (out_stream->time_base.den / 30);
					video_output_packet->stream_index = 0;
					video_output_packet->pos = -1;
					lock (_lock)
					{
						ffmpeg.av_interleaved_write_frame(*output_format_ctx, video_output_packet);
					}
					ffmpeg.av_packet_unref(video_output_packet);
				}
				ffmpeg.av_packet_unref(video_input_packet);
			}
		}
	cleanup:
		ffmpeg.av_frame_free(&srcFrame);
		ffmpeg.av_frame_free(&pFrameYUV);
		ffmpeg.av_packet_unref(video_input_packet);
		ffmpeg.av_packet_unref(video_output_packet);
		return error;
	}

	static unsafe int WriteAudio(CancellationToken token,IntPtr fifo_ptr,IntPtr audio_input_format_ctx_ptr,IntPtr audio_input_codec_ctx_ptr,
	IntPtr audio_output_codec_ctx_ptr,IntPtr resample_context_ptr,IntPtr output_format_ctx_ptr,int streamIndex)
	{
		AVAudioFifo** fifo = (AVAudioFifo**)fifo_ptr;
		AVFormatContext* audio_input_format_ctx = (AVFormatContext*)audio_input_format_ctx_ptr;
		AVCodecContext* audio_input_codec_ctx = (AVCodecContext*)audio_input_codec_ctx_ptr;
		AVCodecContext* audio_output_codec_ctx = (AVCodecContext*)audio_output_codec_ctx_ptr;
		SwrContext* resample_context = (SwrContext*)resample_context_ptr;
		AVFormatContext** output_format_ctx = (AVFormatContext**)output_format_ctx_ptr;

		int error = 0;
		while (!token.IsCancellationRequested)
		{
			int output_frame_size = audio_output_codec_ctx->frame_size;
			while (ffmpeg.av_audio_fifo_size(*fifo) < output_frame_size)
			{
				if ((error = read_decode_convert_and_store(streamIndex, *fifo, audio_input_format_ctx, audio_input_codec_ctx, audio_output_codec_ctx,
				resample_context)) < 0)
				{
					Console.WriteLine($"read_decode_convert_and_store error:{FFmpegHelper.av_err2str(error)}");
					return error;
				}
			}
			while (ffmpeg.av_audio_fifo_size(*fifo) >= output_frame_size
				|| ffmpeg.av_audio_fifo_size(*fifo) > 0)
			{
				if (load_encode_and_write(*fifo, output_format_ctx, audio_output_codec_ctx) < 0)
				{
					return error;
				}
			}
		}
		return error;
	}
	#endregion

	#region 视频
	static unsafe int Open_video_output_file(string fileName,
		AVFormatContext** output_format_context, AVCodecContext** output_codec_context,
		int width, int height, AVStream** out_stream)
	{
		AVCodecContext* avcCtx;
		AVCodec* output_codec;
		AVStream* stream = null;
		int ret = 0;
		ret = ffmpeg.avformat_alloc_output_context2(output_format_context, null, null, fileName);
		if (ret < 0)
		{
			Console.WriteLine("Could not allocate output format context");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}
		ret = ffmpeg.avio_open(&(*output_format_context)->pb, fileName, ffmpeg.AVIO_FLAG_WRITE);
		if (ret < 0)
		{
			Console.WriteLine($"Could not open output file '{fileName}' (error '{FFmpegHelper.av_err2str(ret)}')");
			return ret;
		}
		output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_H264);
		if (output_codec == null)
		{
			Console.WriteLine("Could not find an encoder for 'AV_CODEC_ID_H264'.");
			return ffmpeg.AVERROR_EXIT;
		}

		avcCtx = ffmpeg.avcodec_alloc_context3(output_codec);
		if (avcCtx == null)
		{
			Console.WriteLine("Could not allocate an encoding context.");
			return ffmpeg.AVERROR_EXIT;
		}
		avcCtx->codec_id = AVCodecID.AV_CODEC_ID_H264;
		avcCtx->bit_rate = 2000000;
		avcCtx->width = width;
		avcCtx->height = height;
		AVRational time_base = new AVRational { num = 1, den = 30 };
		avcCtx->time_base = time_base;
		avcCtx->gop_size = 10;
		avcCtx->max_b_frames = 3;
		avcCtx->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
		if (avcCtx->codec_id == AVCodecID.AV_CODEC_ID_H264)
		{
			ffmpeg.av_opt_set(avcCtx->priv_data, "preset", "slow", 0);
			ffmpeg.av_opt_set(avcCtx->priv_data, "tune", "zerolatency", 0);
		}
		if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
		{
			avcCtx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
		}

		ret = ffmpeg.avcodec_open2(avcCtx, output_codec, null);
		if (ret < 0)
		{
			Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(ret)}')");
			goto cleanup;
		}

		stream = ffmpeg.avformat_new_stream(*output_format_context, output_codec);
		if (stream == null)
		{
			Console.WriteLine("Could not allocate stream.");
			ret = ffmpeg.AVERROR_EXIT;
			goto cleanup;
		}
		//stream->time_base = time_base;
		ret = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avcCtx);
		if (ret < 0)
		{
			Console.WriteLine($"Could not initialize stream parameters (error '{FFmpegHelper.av_err2str(ret)}')");
			goto cleanup;
		}
		*output_codec_context = avcCtx;
		*out_stream = stream;
		return ret;

	cleanup:
		ffmpeg.avio_closep(&(*output_format_context)->pb);
		ffmpeg.avformat_free_context(*output_format_context);
		*output_format_context = null;
		return ret < 0 ? ret : ffmpeg.AVERROR_EXIT;
	}

	static unsafe int Init_SwsContext(AVCodecContext** input_codec_context, AVCodecContext** output_codec_context,
		SwsContext** sws_context)
	{
		SwsContext* sws_ctx = ffmpeg.sws_getContext((*input_codec_context)->width,
			(*input_codec_context)->height,
			(*input_codec_context)->pix_fmt,
			(*input_codec_context)->width,
			(*input_codec_context)->height,
			AVPixelFormat.AV_PIX_FMT_YUV420P,
			ffmpeg.SWS_BICUBIC, null, null, null);
		if (sws_ctx == null)
		{
			Console.WriteLine("Could not initialize the conversion context");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}

		*sws_context = sws_ctx;
		return 0;
	}

	#endregion

	#region 音频
	static unsafe int open_audio_output_file(string filename, AVCodecContext* input_codec_context, AVFormatContext** output_format_context,
		AVCodecContext** output_codec_context)
	{
		AVCodecContext* avctx = null;
		AVStream* stream = null;
		AVCodec* output_codec = null;
		int error = 0;

		if ((error = ffmpeg.avformat_alloc_output_context2(output_format_context, null, null, filename)) < 0)
		{
			Console.WriteLine($"Could not alloc output context '{filename}' (error '{FFmpegHelper.av_err2str(error)}')");
			return error;
		}
		if ((error = ffmpeg.avio_open(&(*output_format_context)->pb, filename, ffmpeg.AVIO_FLAG_WRITE)) < 0)
		{
			Console.WriteLine($"Could not open output file '{filename}' (error '{FFmpegHelper.av_err2str(error)}')");
			return error;
		}
		if ((output_codec = ffmpeg.avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC)) == null)
		{
			Console.WriteLine("Could not find an AAC encoder");
			goto cleanup;
		}

		avctx = ffmpeg.avcodec_alloc_context3(output_codec);
		if (avctx == null)
		{
			Console.WriteLine("Could not allocate an encoding context");
			error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
			goto cleanup;
		}

		avctx->ch_layout.nb_channels = OUTPUT_CHANNELS;
		avctx->channel_layout = (ulong)ffmpeg.av_get_default_channel_layout(OUTPUT_CHANNELS);
		avctx->sample_rate = input_codec_context->sample_rate;
		avctx->sample_fmt = output_codec->sample_fmts[0];
		avctx->bit_rate = OUTPUT_BIT_RATE;
		avctx->strict_std_compliance = ffmpeg.FF_COMPLIANCE_EXPERIMENTAL;

		if (((*output_format_context)->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) == ffmpeg.AVFMT_GLOBALHEADER)
		{
			avctx->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;
		}

		if ((error = ffmpeg.avcodec_open2(avctx, output_codec, null)) < 0)
		{
			Console.WriteLine($"Could not open output codec (error '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}

		if ((stream = ffmpeg.avformat_new_stream(*output_format_context, null)) == null)
		{
			Console.WriteLine("Could not create new stream");
			error = ffmpeg.AVERROR(ffmpeg.ENOMEM);
			goto cleanup;
		}


		stream->time_base.den = input_codec_context->sample_rate;
		stream->time_base.num = 1;
		error = ffmpeg.avcodec_parameters_from_context(stream->codecpar, avctx);
		if (error < 0)
		{
			Console.WriteLine("Could not initialize stream parameters");
			goto cleanup;
		}

		*output_codec_context = avctx;
		return 0;
	cleanup:

		ffmpeg.avcodec_free_context(&avctx);
		ffmpeg.avio_closep(&(*output_format_context)->pb);
		ffmpeg.avformat_free_context(*output_format_context);
		*output_format_context = null;

		return error < 0 ? error : ffmpeg.AVERROR_EXIT;
	}
	static unsafe int init_resampler(AVCodecContext* input_codec_context, AVCodecContext* output_codec_context,
		SwrContext** resample_context)
	{
		int error = 0;

		*resample_context = ffmpeg.swr_alloc_set_opts(null,
			ffmpeg.av_get_default_channel_layout(output_codec_context->ch_layout.nb_channels),
			output_codec_context->sample_fmt,
			output_codec_context->sample_rate,
			ffmpeg.av_get_default_channel_layout(input_codec_context->ch_layout.nb_channels),
			input_codec_context->sample_fmt,
			input_codec_context->sample_rate,
			0, null);

		if (*resample_context == null)
		{
			Console.WriteLine("Could not allocate resample context");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}
		if ((error = ffmpeg.swr_init(*resample_context)) < 0)
		{
			Console.WriteLine("Could not open resample context");
			ffmpeg.swr_free(resample_context);
			return error;
		}
		return 0;
	}
	static unsafe int init_fifo(AVAudioFifo** fifo, AVCodecContext* output_codec_context)
	{
		if ((*fifo = ffmpeg.av_audio_fifo_alloc(output_codec_context->sample_fmt, output_codec_context->ch_layout.nb_channels, 1)) == null)
		{
			Console.WriteLine("Could not allocate FIFO");
			return ffmpeg.AVERROR(ffmpeg.ENOMEM);
		}

		return 0;
	}
	static unsafe int write_output_file_header(AVFormatContext* output_format_context)
	{
		int error = ffmpeg.avformat_write_header(output_format_context, null);

		if (error < 0)
		{
			Console.WriteLine($"Could not write output file header (error '{FFmpegHelper.av_err2str(error)}'");
			return error;
		}

		return 0;
	}
	static unsafe int write_output_file_trailer(AVFormatContext* output_format_context)
	{
		int error = ffmpeg.av_write_trailer(output_format_context);
		if (error < 0)
		{
			Console.WriteLine($"Could not write output file trailer (error '{FFmpegHelper.av_err2str(error)}')");
			return error;
		}

		return 0;
	}
	static unsafe int read_decode_convert_and_store(int streamIndex,AVAudioFifo* fifo, AVFormatContext* input_format_context, AVCodecContext* input_codec_context,
		 AVCodecContext* output_codec_context, SwrContext* resampler_context)
	{
		int error = 0;
		byte** converted_input_samples = null;
		AVFrame* input_frame = ffmpeg.av_frame_alloc();
		if (input_frame == null)
		{
			Console.WriteLine("Could not allocate input frame");
			goto cleanup;
		}
		AVPacket* input_packet = ffmpeg.av_packet_alloc();
		if (input_packet == null)
		{
			Console.WriteLine("Could not allocate packet");
			goto cleanup;
		}
		if ((error = ffmpeg.av_read_frame(input_format_context, input_packet)) < 0)
		{

			Console.WriteLine($"Could not raed frame (eror '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}
		if (input_packet->stream_index != streamIndex)
		{
			error = 0;
			goto cleanup;
		}
		if ((error = ffmpeg.avcodec_send_packet(input_codec_context, input_packet)) < 0)
		{
			Console.WriteLine($"Could not send packet for decoding (error '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}
		if ((error = ffmpeg.avcodec_receive_frame(input_codec_context, input_frame)) < 0)
		{
			if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
			{
				error = 0;
				goto cleanup;
			}
			Console.WriteLine($"Could not decode frame (error '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}
		converted_input_samples = (byte**)ffmpeg.av_calloc((ulong)output_codec_context->ch_layout.nb_channels, (ulong)IntPtr.Size);
		if ((error = ffmpeg.av_samples_alloc(converted_input_samples, null, output_codec_context->ch_layout.nb_channels,
	   input_frame->nb_samples, output_codec_context->sample_fmt, 0)) < 0)
		{
			Console.WriteLine($"Could not allocate converted input samples (error '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}
		if ((error = ffmpeg.swr_convert(resampler_context, converted_input_samples,
		input_frame->nb_samples,
		input_frame->extended_data, input_frame->nb_samples)) < 0)
		{
			Console.WriteLine($"Could not convert input samples (error '{FFmpegHelper.av_err2str(error)}')");
			goto cleanup;
		}
		if ((error = ffmpeg.av_audio_fifo_realloc(fifo, ffmpeg.av_audio_fifo_size(fifo) + input_frame->nb_samples)) < 0)
		{
			Console.WriteLine("Could not reallocate FIFO");
			goto cleanup;
		}
		if (ffmpeg.av_audio_fifo_write(fifo, (void**)converted_input_samples, input_frame->nb_samples) < input_frame->nb_samples)
		{
			Console.WriteLine("Could not write data to FIFO");
			goto cleanup;
		}
	cleanup:
		if (converted_input_samples != null)
		{
			ffmpeg.av_freep(&converted_input_samples[0]);
			ffmpeg.av_free(converted_input_samples);
		}

		ffmpeg.av_frame_free(&input_frame);
		ffmpeg.av_packet_free(&input_packet);
		return error;

	}

	static unsafe int load_encode_and_write(AVAudioFifo* fifo, AVFormatContext** output_format_context, AVCodecContext* output_codec_context)
	{
		int error = 0;
		AVPacket* output_packet = ffmpeg.av_packet_alloc();
		AVFrame* output_frame = ffmpeg.av_frame_alloc();
		int frame_size = Math.Min(ffmpeg.av_audio_fifo_size(fifo), output_codec_context->frame_size);
		output_frame->nb_samples = frame_size;
		output_frame->ch_layout = output_codec_context->ch_layout;
		output_frame->format = (int)output_codec_context->sample_fmt;
		output_frame->sample_rate = output_codec_context->sample_rate;
		if ((error = ffmpeg.av_frame_get_buffer(output_frame, 0)) < 0)
		{
			Console.WriteLine($"Could not allocate output frame samples (error '{FFmpegHelper.av_err2str(error)}')");
			ffmpeg.av_frame_free(&output_frame);
			goto cleanup;
		}
		void* ptr = &output_frame->data;
		if (ffmpeg.av_audio_fifo_read(fifo, (void**)ptr, frame_size) < frame_size)
		{
			error = ffmpeg.AVERROR_EXIT;
			goto cleanup;
		}
		if (output_frame != null)
		{
			output_frame->pts = AudioPts;
			AudioPts += output_frame->nb_samples;
		}
		if ((error = ffmpeg.avcodec_send_frame(output_codec_context, output_frame)) < 0)
		{
			Console.WriteLine($"Could not send frame :{FFmpegHelper.av_err2str(error)}");
			goto cleanup;
		}
		if ((error = ffmpeg.avcodec_receive_packet(output_codec_context, output_packet)) < 0)
		{
			if (error == ffmpeg.AVERROR(ffmpeg.EAGAIN) || error == ffmpeg.AVERROR_EOF)
			{
				error = 0;
				goto cleanup;
			}
			Console.WriteLine($"Could not receive packet :{FFmpegHelper.av_err2str(error)}");
			goto cleanup;
		}
		lock (_lock)
		{
			if ((error = ffmpeg.av_interleaved_write_frame(*output_format_context, output_packet)) < 0)
			{
				Console.WriteLine($"Could not write frame (error '{FFmpegHelper.av_err2str(error)}')");
				goto cleanup;
			}
		}
	cleanup:
		ffmpeg.av_packet_unref(output_packet);
		ffmpeg.av_frame_unref(output_frame);
		return error;
	}

	#endregion
}